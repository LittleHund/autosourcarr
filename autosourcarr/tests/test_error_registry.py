from app.error_registry import ErrorRegistry


def test_error_registry_deduplicates_and_resolves():
    registry = ErrorRegistry(max_entries=20)
    first = registry.record(
        "Private source host", "connection failed", key="source-host-pages",
        path="/api/v1/chapters/1/pages", method="GET", status_code=503,
    )
    second = registry.record(
        "Private source host", "connection failed", key="source-host-pages",
        path="/api/v1/chapters/1/pages", method="GET", status_code=503,
    )
    assert first == second
    snapshot = registry.snapshot(include_resolved=False)
    assert snapshot["active_error_count"] == 1
    assert snapshot["items"][0]["count"] == 2
    assert registry.resolve(key="source-host-pages") == 1
    assert registry.snapshot(include_resolved=False)["active_count"] == 0
    assert registry.snapshot(include_resolved=True)["items"][0]["resolved"] is True


def test_error_registry_resolves_recovered_request_prefix():
    registry = ErrorRegistry(max_entries=20)
    registry.record(
        "AutoSourcarr", "boom",
        key="unhandled|GET|/api/test|RuntimeError|boom",
        path="/api/test", method="GET",
    )
    assert registry.resolve_prefix("unhandled|GET|/api/test|") == 1
    assert registry.snapshot(include_resolved=False)["active_count"] == 0
