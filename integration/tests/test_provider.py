import json
import socket
import threading
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

from course_integration.provider import (
    RESPONSE_INVALID,
    TRANSPORT_RETRYABLE,
    OutboxMessage,
    build_payment_request,
    classify_response,
    classify_status,
    send_payment,
)

MESSAGE = OutboxMessage(
    outbox_id="0b8b7a5e-8d0a-4c5a-9c55-36b3c2e0d001",
    lease_version=3,
    external_request_id="ext-5b1f8b8e-6a55-4bc1-8f59-0e3f6f6c0a11",
    correlation_id="1f7d3c8e-7c4b-4a0d-9d7e-3a6f5f0c2b22",
    amount="1000.00",
    currency="RUB",
)


class PaymentRequestTests(unittest.TestCase):
    def test_exact_body_and_headers(self):
        request = build_payment_request(MESSAGE)
        self.assertEqual(request.path, "/payments")
        self.assertEqual(
            request.body,
            b'{"operationId":"ext-5b1f8b8e-6a55-4bc1-8f59-0e3f6f6c0a11","amount":"1000.00","currency":"RUB"}',
        )
        self.assertEqual(
            request.headers,
            {
                "Content-Type": "application/json",
                "Idempotency-Key": MESSAGE.external_request_id,
                "X-Correlation-ID": MESSAGE.correlation_id,
            },
        )

    def test_retries_of_one_delivery_are_byte_identical(self):
        retry = OutboxMessage(**{**MESSAGE.__dict__, "lease_version": 4})
        self.assertEqual(build_payment_request(MESSAGE), build_payment_request(retry))

    def test_legacy_operation_id_equals_idempotency_key(self):
        request = build_payment_request(MESSAGE)
        self.assertEqual(json.loads(request.body)["operationId"], request.headers["Idempotency-Key"])


class ClassificationTests(unittest.TestCase):
    def test_retryable_statuses(self):
        for status in (408, 429, 500, 502, 503, 504, 599):
            with self.subTest(status=status):
                self.assertEqual(classify_status(status), f"http.{status}.retryable")

    def test_terminal_statuses(self):
        for status in (200, 201, 204, 301, 302, 307, 400, 401, 403, 404, 409, 422):
            with self.subTest(status=status):
                self.assertEqual(classify_status(status), f"http.{status}.terminal")

    def test_accepted_response(self):
        result = classify_response(202, b'{"providerPaymentId":"provider-1","status":"ACCEPTED"}')
        self.assertTrue(result.accepted)
        self.assertEqual(result.provider_payment_id, "provider-1")
        self.assertIsNone(result.error_code)

    def test_invalid_accepted_bodies_are_terminal(self):
        for body in (
            b"",
            b"not json",
            b"\xff",
            b"[]",
            b'{"providerPaymentId":"p","status":"ACCEPTED","extra":1}',
            b'{"providerPaymentId":"p","status":"PENDING"}',
            b'{"providerPaymentId":"","status":"ACCEPTED"}',
            b'{"providerPaymentId":1,"status":"ACCEPTED"}',
            b'{"providerPaymentId":"a\\nb","status":"ACCEPTED"}',
            b'{"providerPaymentId":"a","providerPaymentId":"b","status":"ACCEPTED"}',
        ):
            with self.subTest(body=body):
                result = classify_response(202, body)
                self.assertFalse(result.accepted)
                self.assertEqual(result.error_code, RESPONSE_INVALID)

    def test_non_202_success_is_not_accepted(self):
        result = classify_response(200, b'{"providerPaymentId":"p","status":"ACCEPTED"}')
        self.assertEqual(result.error_code, "http.200.terminal")


class _Provider(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    behaviour = "accept"
    received: list = []

    def log_message(self, *args):
        return

    def do_POST(self):
        length = int(self.headers.get("Content-Length", "0"))
        body = self.rfile.read(length)
        type(self).received.append((self.path, dict(self.headers.items()), body))
        if type(self).behaviour == "slow":
            threading.Event().wait(1.0)
        if type(self).behaviour == "redirect":
            payload, status = b"", 302
        elif type(self).behaviour == "busy":
            payload, status = b'{"code":"busy"}', 503
        else:
            payload, status = b'{"providerPaymentId":"provider-9","status":"ACCEPTED"}', 202
        self.send_response(status)
        if status == 302:
            self.send_header("Location", "/elsewhere")
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)


class SendPaymentTests(unittest.TestCase):
    def setUp(self):
        _Provider.received = []
        _Provider.behaviour = "accept"
        self.server = ThreadingHTTPServer(("127.0.0.1", 0), _Provider)
        self.server.daemon_threads = True
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()
        self.base = f"http://127.0.0.1:{self.server.server_address[1]}"

    def tearDown(self):
        self.server.shutdown()
        self.server.server_close()

    def test_one_call_is_one_attempt_with_exact_bytes(self):
        request = build_payment_request(MESSAGE)
        result = send_payment(self.base, request, 2.0)
        self.assertTrue(result.accepted)
        self.assertEqual(len(_Provider.received), 1)
        path, headers, body = _Provider.received[0]
        self.assertEqual(path, "/payments")
        self.assertEqual(body, request.body)
        self.assertEqual(headers["Idempotency-Key"], MESSAGE.external_request_id)
        self.assertEqual(headers["X-Correlation-ID"], MESSAGE.correlation_id)
        self.assertEqual(headers["Content-Type"], "application/json")

    def test_redirect_is_not_followed(self):
        _Provider.behaviour = "redirect"
        result = send_payment(self.base, build_payment_request(MESSAGE), 2.0)
        self.assertEqual(result.error_code, "http.302.terminal")
        self.assertEqual(len(_Provider.received), 1)

    def test_server_error_is_retryable_and_not_retried_locally(self):
        _Provider.behaviour = "busy"
        result = send_payment(self.base, build_payment_request(MESSAGE), 2.0)
        self.assertEqual(result.error_code, "http.503.retryable")
        self.assertEqual(len(_Provider.received), 1)

    def test_timeout_is_transport_retryable(self):
        _Provider.behaviour = "slow"
        result = send_payment(self.base, build_payment_request(MESSAGE), 0.2)
        self.assertEqual(result.error_code, TRANSPORT_RETRYABLE)

    def test_connection_refused_is_transport_retryable(self):
        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as probe:
            probe.bind(("127.0.0.1", 0))
            port = probe.getsockname()[1]
        result = send_payment(f"http://127.0.0.1:{port}", build_payment_request(MESSAGE), 0.5)
        self.assertEqual(result.error_code, TRANSPORT_RETRYABLE)


if __name__ == "__main__":
    unittest.main()
