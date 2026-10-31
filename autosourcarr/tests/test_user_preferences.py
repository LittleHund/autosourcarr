from app.main import _ui_user_context


class FakeRequest:
    def __init__(self, headers):
        self.headers = {str(k).lower(): v for k, v in headers.items()}


def test_authelia_remote_user_is_preferred_for_ui_preferences():
    ctx = _ui_user_context(FakeRequest({"Remote-User": "ExampleUser", "X-Forwarded-User": "Other"}))
    assert ctx["authelia"] is True
    assert ctx["user"] == "ExampleUser"
    assert ctx["key"] == "authelia:exampleuser"
    assert ctx["header"] == "Remote-User"


def test_forwarded_user_alias_is_supported():
    ctx = _ui_user_context(FakeRequest({"X-Auth-Request-User": "alice"}))
    assert ctx["authelia"] is True
    assert ctx["key"] == "authelia:alice"


def test_missing_proxy_identity_uses_explicit_local_fallback():
    ctx = _ui_user_context(FakeRequest({}))
    assert ctx["authelia"] is False
    assert ctx["key"] == "local:default"
    assert ctx["user"] == "Local user"


def test_forwarded_username_variant_is_supported():
    ctx = _ui_user_context(FakeRequest({"X-Forwarded-Username": "bob"}))
    assert ctx["authelia"] is True
    assert ctx["key"] == "authelia:bob"
    assert ctx["header"] == "X-Forwarded-Username"


def test_missing_user_header_reports_proxy_diagnostics():
    ctx = _ui_user_context(FakeRequest({"X-Forwarded-Proto": "https", "X-Forwarded-Host": "autosourcarr.example"}))
    assert ctx["authelia"] is False
    assert ctx["proxy_detected"] is True
    assert "x-forwarded-proto" in [x.lower() for x in ctx["proxy_headers"]]
