import hashlib
import hmac
import http.client
import http.server
import io
import json
import threading
import unittest

from course_integration.adapter import (
    DEPENDENCY_UNAVAILABLE,
    MAX_API_RESPONSE_BYTES,
    CallbackServer,
    ReceiptAdapter,
    Reply,
    _handler_class,
    sign_callback,
)
from course_integration.config import adapter_settings
from course_integration.logs import Logger

CAPABILITY = "cap-Zx9_-token"
TOKEN = "header.payload.signature"
SECRET = "hmac secret "
ENV = {
    "PROVIDER_CALLBACK_CAPABILITY": CAPABILITY,
    "PROVIDER_CALLBACK_TOKEN": TOKEN,
    "PROVIDER_HMAC_SECRET": SECRET,
    "RECEIPT_API_URL": "http://gateway:8080/api/receipt/accept",
    "RECEIPT_ADAPTER_PORT": "8082",
}

LEGACY = {
    "providerPaymentId": "provider-77",
    "operationId": "ext-42",
    "result": "REJECTED",
    "message": "Very private provider message",
    "occurredAt": "2026-09-04T12:00:00.123Z",
}
RAW = json.dumps(LEGACY).encode("utf-8")


class RecordingTransport:
    def __init__(self, reply=None, error=None):
        self.reply = reply or Reply(200, b'{"status":"ok"}', "application/json; charset=utf-8")
        self.error = error
        self.calls = []

    def __call__(self, url, body, headers, timeout):
        self.calls.append((url, body, headers, timeout))
        if self.error:
            raise self.error
        return self.reply


class SignCallbackTests(unittest.TestCase):
    def setUp(self):
        self.settings = adapter_settings(ENV)

    def test_headers_and_signature_over_exact_body(self):
        signed = sign_callback(RAW, self.settings)
        expected_body = (
            b'{"externalRequestId":"ext-42","messageId":"provider-77",'
            b'"occurredAt":"2026-09-04T12:00:00.123Z","outcome":"REJECTED",'
            b'"providerPaymentId":"provider-77","version":1}'
        )
        self.assertEqual(signed.body, expected_body)
        self.assertEqual(signed.headers["Authorization"], f"Bearer {TOKEN}")
        self.assertEqual(signed.headers["Idempotency-Key"], "provider-77")
        self.assertEqual(signed.headers["X-Action-Version"], "1")
        self.assertEqual(signed.headers["Content-Type"], "application/json")
        digest = hmac.new(SECRET.encode("utf-8"), expected_body, hashlib.sha256).hexdigest()
        self.assertEqual(signed.headers["X-Provider-Signature"], "v1=" + digest)

    def test_duplicate_callback_produces_identical_request(self):
        self.assertEqual(sign_callback(RAW, self.settings), sign_callback(RAW, self.settings))

    def test_message_never_reaches_the_platform(self):
        self.assertNotIn(b"Very private", sign_callback(RAW, self.settings).body)


class AdapterHandlingTests(unittest.TestCase):
    def setUp(self):
        self.log = io.StringIO()
        self.settings = adapter_settings(ENV)

    def adapter(self, transport):
        return ReceiptAdapter(self.settings, Logger("receipt-adapter", self.log), transport)

    def test_route_requires_exact_capability(self):
        adapter = self.adapter(RecordingTransport())
        self.assertTrue(adapter.route_matches("POST", "/callbacks/provider-v02/" + CAPABILITY))
        for method, path in (
            ("POST", "/callbacks/provider-v02/wrong"),
            ("POST", "/callbacks/provider-v02/" + CAPABILITY + "x"),
            ("POST", "/callbacks/provider-v02/" + CAPABILITY[:-1]),
            ("POST", "/callbacks/provider-v02/"),
            ("POST", "/callbacks/provider-v01/" + CAPABILITY),
            ("POST", "/callbacks/provider-v02/" + CAPABILITY + "?a=1"),
            ("GET", "/callbacks/provider-v02/" + CAPABILITY),
        ):
            with self.subTest(method=method, path=path):
                self.assertFalse(adapter.route_matches(method, path))

    def test_content_length_rules(self):
        self.assertEqual(ReceiptAdapter.content_length(["10"]), 10)
        for values in ([], ["0"], ["65537"], ["abc"], ["10", "10"], ["-1"]):
            with self.subTest(values=values):
                self.assertIsNone(ReceiptAdapter.content_length(values))

    def test_invalid_callback_is_400_without_calling_api(self):
        transport = RecordingTransport()
        reply = self.adapter(transport).handle(json.dumps({**LEGACY, "extra": 1}).encode())
        self.assertEqual(reply, Reply(400))
        self.assertEqual(transport.calls, [])

    def test_api_reply_is_passed_through(self):
        for status, body in ((200, b'{"status":"ok"}'), (409, b'{"code":"idempotency.conflict"}'),
                             (422, b'{"code":"receipt.external_request_not_found"}'), (500, b"{}")):
            with self.subTest(status=status):
                upstream = Reply(status, body, "application/json; charset=utf-8")
                transport = RecordingTransport(upstream)
                self.assertEqual(self.adapter(transport).handle(RAW), upstream)
                url, sent, headers, _ = transport.calls[0]
                self.assertEqual(url, ENV["RECEIPT_API_URL"])
                self.assertEqual(headers["Idempotency-Key"], "provider-77")

    def test_unreachable_api_is_503_dependency_unavailable(self):
        for error in (ConnectionRefusedError(), TimeoutError(), http.client.RemoteDisconnected()):
            with self.subTest(error=type(error).__name__):
                reply = self.adapter(RecordingTransport(error=error)).handle(RAW)
                self.assertEqual(reply.status, 503)
                self.assertEqual(json.loads(reply.body), {"status": "error", "code": "dependency.unavailable"})
                self.assertEqual(reply.body, DEPENDENCY_UNAVAILABLE)

    def test_unexpected_failure_answers_500_instead_of_dropping_the_connection(self):
        reply = self.adapter(RecordingTransport(error=RuntimeError("boom with value 42"))).handle(RAW)
        self.assertEqual(reply, Reply(500))
        self.assertIn('"failure":"RuntimeError"', self.log.getvalue())
        self.assertNotIn("boom with value", self.log.getvalue())

    def test_oversized_api_response_is_not_relayed_truncated(self):
        class Oversized(http.server.BaseHTTPRequestHandler):
            def log_message(self, *args):
                return

            def do_POST(self):
                self.rfile.read(int(self.headers["Content-Length"]))
                body = b"x" * (MAX_API_RESPONSE_BYTES + 10)
                self.send_response(200)
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)

        server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Oversized)
        threading.Thread(target=server.serve_forever, daemon=True).start()
        try:
            settings = adapter_settings({**ENV, "RECEIPT_API_URL": f"http://127.0.0.1:{server.server_address[1]}/api/receipt/accept"})
            reply = ReceiptAdapter(settings, Logger("receipt-adapter", self.log)).handle(RAW)
        finally:
            server.shutdown()
            server.server_close()
        self.assertEqual(reply.status, 503)
        self.assertEqual(reply.body, DEPENDENCY_UNAVAILABLE)

    def test_logs_contain_no_message_body_or_secrets(self):
        adapter = self.adapter(RecordingTransport())
        adapter.handle(RAW)
        adapter.handle(json.dumps({**LEGACY, "message": "leak\nme"}).encode())
        text = self.log.getvalue()
        for forbidden in (LEGACY["message"], "leak", TOKEN, SECRET.strip(), CAPABILITY, RAW.decode()):
            self.assertNotIn(forbidden, text)


class AdapterServerTests(unittest.TestCase):
    def setUp(self):
        self.log = io.StringIO()
        self.transport = RecordingTransport(Reply(200, b'{"status":"ok","outcome":"RECEIVED"}', "application/json"))
        adapter = ReceiptAdapter(adapter_settings(ENV), Logger("receipt-adapter", self.log), self.transport)
        self.adapter_under_test = adapter
        self.ready = True
        self.server = CallbackServer(
            ("127.0.0.1", 0), _handler_class(adapter, lambda: self.ready)
        )
        threading.Thread(target=self.server.serve_forever, daemon=True).start()
        self.port = self.server.server_address[1]

    def tearDown(self):
        self.server.shutdown()
        self.server.server_close()

    def request(self, method, path, body=None, headers=None):
        connection = http.client.HTTPConnection("127.0.0.1", self.port, timeout=5)
        try:
            connection.request(method, path, body=body, headers=headers or {})
            response = connection.getresponse()
            return response.status, response.read(), response.getheader("Content-Type")
        finally:
            connection.close()

    def test_wrong_capability_is_404_with_empty_body(self):
        status, body, _ = self.request("POST", "/callbacks/provider-v02/wrong", RAW, {"Content-Type": "application/json"})
        self.assertEqual((status, body), (404, b""))
        self.assertEqual(self.transport.calls, [])

    def test_other_paths_and_methods_are_404(self):
        for method, path in (("GET", "/"), ("GET", "/callbacks/provider-v02/" + CAPABILITY), ("PUT", "/health")):
            with self.subTest(method=method, path=path):
                status, body, _ = self.request(method, path)
                self.assertEqual((status, body), (404, b""))

    def test_malformed_json_is_400_with_empty_body(self):
        status, body, _ = self.request("POST", "/callbacks/provider-v02/" + CAPABILITY, b"{not json", {"Content-Type": "application/json"})
        self.assertEqual((status, body), (400, b""))

    def test_oversized_body_is_400(self):
        status, _, _ = self.request("POST", "/callbacks/provider-v02/" + CAPABILITY, b"x" * (64 * 1024 + 1))
        self.assertEqual(status, 400)

    def test_valid_callback_returns_api_status_body_and_media_type(self):
        status, body, content_type = self.request(
            "POST", "/callbacks/provider-v02/" + CAPABILITY, RAW, {"Content-Type": "application/json"}
        )
        self.assertEqual(status, 200)
        self.assertEqual(body, b'{"status":"ok","outcome":"RECEIVED"}')
        self.assertEqual(content_type, "application/json")
        self.assertEqual(len(self.transport.calls), 1)
        self.assertNotIn(CAPABILITY, self.log.getvalue())


if __name__ == "__main__":
    unittest.main()
