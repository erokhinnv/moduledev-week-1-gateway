import hashlib
import hmac
import json
import unittest

from course_integration.receipt import (
    CallbackRejected,
    body_hash,
    canonical_bytes,
    parse_legacy_callback,
    signature,
    to_receipt,
)


def legacy(**overrides):
    value = {
        "providerPaymentId": "provider-123",
        "operationId": "external-123",
        "result": "COMPLETED",
        "message": "Payment completed",
        "occurredAt": "2026-09-04T12:00:00Z",
    }
    value.update(overrides)
    return value


def raw(value) -> bytes:
    return json.dumps(value, ensure_ascii=False).encode("utf-8")


class LegacyCallbackValidationTests(unittest.TestCase):
    def test_valid_callback_is_accepted(self):
        self.assertEqual(parse_legacy_callback(raw(legacy()))["result"], "COMPLETED")

    def test_rejected_result_and_empty_message_are_valid(self):
        callback = parse_legacy_callback(raw(legacy(result="REJECTED", message="")))
        self.assertEqual(callback["result"], "REJECTED")

    def test_unknown_field_is_rejected(self):
        with self.assertRaises(CallbackRejected):
            parse_legacy_callback(raw({**legacy(), "status": "OK"}))

    def test_missing_field_is_rejected(self):
        for field in ("providerPaymentId", "operationId", "result", "message", "occurredAt"):
            value = legacy()
            del value[field]
            with self.subTest(field=field), self.assertRaises(CallbackRejected):
                parse_legacy_callback(raw(value))

    def test_cr_and_lf_are_rejected_in_every_string_field(self):
        for field in ("providerPaymentId", "operationId", "message", "occurredAt"):
            for bad in ("a\rb", "a\nb"):
                with self.subTest(field=field, bad=repr(bad)), self.assertRaises(CallbackRejected):
                    parse_legacy_callback(raw(legacy(**{field: bad})))

    def test_wrong_types_are_rejected(self):
        cases = {
            "providerPaymentId": 123,
            "operationId": True,
            "message": None,
            "occurredAt": 1757000000,
            "result": ["COMPLETED"],
        }
        for field, bad in cases.items():
            with self.subTest(field=field), self.assertRaises(CallbackRejected):
                parse_legacy_callback(raw(legacy(**{field: bad})))

    def test_result_outside_contract_is_rejected(self):
        for bad in ("completed", "FAILED", ""):
            with self.subTest(result=bad), self.assertRaises(CallbackRejected):
                parse_legacy_callback(raw(legacy(result=bad)))

    def test_length_limits(self):
        parse_legacy_callback(raw(legacy(providerPaymentId="p" * 128, message="m" * 500)))
        for field, bad in (("providerPaymentId", "p" * 129), ("operationId", ""), ("message", "m" * 501)):
            with self.subTest(field=field), self.assertRaises(CallbackRejected):
                parse_legacy_callback(raw(legacy(**{field: bad})))

    def test_occurred_at_must_be_utc_rfc3339(self):
        for bad in (
            "2026-09-04T12:00:00+00:00",
            "2026-09-04T12:00:00z",
            "2026-09-04 12:00:00Z",
            "2026-02-30T12:00:00Z",
            "2026-09-04T24:00:00Z",
            "2026-09-04T12:00:00.Z",
            "2026-09-04T12:00:00." + "1" * 50 + "Z",
        ):
            with self.subTest(value=bad), self.assertRaises(CallbackRejected):
                parse_legacy_callback(raw(legacy(occurredAt=bad)))

    def test_malformed_json_and_non_objects_are_rejected(self):
        for body in (
            b"",
            b"{",
            b"[]",
            b'"text"',
            b'{"providerPaymentId":"a","providerPaymentId":"b","operationId":"x","result":"COMPLETED","message":"","occurredAt":"2026-09-04T12:00:00Z"}',
            b'{"providerPaymentId":NaN}',
            b"\xef\xbb\xbf" + raw(legacy()),
            b"\xff\xfe",
        ):
            with self.subTest(body=body[:40]), self.assertRaises(CallbackRejected):
                parse_legacy_callback(body)


class ReceiptMappingTests(unittest.TestCase):
    def test_all_fields_are_mapped_and_message_is_dropped(self):
        receipt = to_receipt(parse_legacy_callback(raw(legacy(result="REJECTED"))))
        self.assertEqual(
            receipt,
            {
                "externalRequestId": "external-123",
                "messageId": "provider-123",
                "occurredAt": "2026-09-04T12:00:00Z",
                "outcome": "REJECTED",
                "providerPaymentId": "provider-123",
                "version": 1,
            },
        )
        self.assertNotIn("message", receipt)

    def test_timestamp_string_is_preserved_exactly(self):
        for value in (
            "2026-09-04T12:00:00Z",
            "2026-09-04T12:00:00.1Z",
            "2026-09-04T12:00:00.120Z",
            "2026-09-04T12:00:00.123456789Z",
        ):
            with self.subTest(value=value):
                receipt = to_receipt(parse_legacy_callback(raw(legacy(occurredAt=value))))
                self.assertEqual(receipt["occurredAt"], value)

    def test_duplicate_and_conflicting_callbacks_share_the_message_id(self):
        first = to_receipt(parse_legacy_callback(raw(legacy(result="REJECTED"))))
        conflict = to_receipt(parse_legacy_callback(raw(legacy(result="COMPLETED", message="x"))))
        self.assertEqual(first["messageId"], conflict["messageId"])
        self.assertNotEqual(canonical_bytes(first), canonical_bytes(conflict))


class CanonicalBytesAndSignatureTests(unittest.TestCase):
    RECEIPT = {
        "version": 1,
        "providerPaymentId": "provider-123",
        "outcome": "COMPLETED",
        "occurredAt": "2026-09-04T12:00:00Z",
        "messageId": "provider-123",
        "externalRequestId": "external-123",
    }

    def test_compact_sorted_bytes_without_bom_or_trailing_lf(self):
        expected = (
            b'{"externalRequestId":"external-123","messageId":"provider-123",'
            b'"occurredAt":"2026-09-04T12:00:00Z","outcome":"COMPLETED",'
            b'"providerPaymentId":"provider-123","version":1}'
        )
        self.assertEqual(canonical_bytes(self.RECEIPT), expected)

    def test_non_ascii_is_utf8_not_escaped(self):
        body = canonical_bytes({**self.RECEIPT, "externalRequestId": "внешний"})
        self.assertIn("внешний".encode("utf-8"), body)
        self.assertNotIn(b"\\u", body)

    def test_matches_the_published_serialization_call(self):
        published = json.dumps(
            self.RECEIPT, ensure_ascii=False, separators=(",", ":"), sort_keys=True
        ).encode("utf-8")
        self.assertEqual(canonical_bytes(self.RECEIPT), published)

    def test_hmac_uses_raw_utf8_secret_over_exact_bytes(self):
        secret = " секрет with spaces \t"
        body = canonical_bytes(self.RECEIPT)
        expected = hmac.new(secret.encode("utf-8"), body, hashlib.sha256).hexdigest()
        self.assertEqual(signature(secret.encode("utf-8"), body), "v1=" + expected)
        self.assertNotEqual(signature(secret.strip().encode("utf-8"), body), "v1=" + expected)

    def test_signature_format_is_lowercase_hex(self):
        value = signature(b"k", b"body")
        self.assertRegex(value, r"^v1=[0-9a-f]{64}$")

    def test_signature_changes_with_any_byte(self):
        body = canonical_bytes(self.RECEIPT)
        self.assertNotEqual(signature(b"k", body), signature(b"k", body + b" "))

    def test_body_hash_is_sha256_of_exact_bytes(self):
        body = canonical_bytes(self.RECEIPT)
        self.assertEqual(body_hash(body), hashlib.sha256(body).hexdigest())


if __name__ == "__main__":
    unittest.main()
