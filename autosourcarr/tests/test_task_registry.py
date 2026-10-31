from app.task_registry import TaskRegistry


def test_task_registry_tracks_progress_and_running_count():
    registry = TaskRegistry()
    task_id = registry.create("Repair missing metadata", total=58)
    first = registry.snapshot()
    assert first["running"] == 1
    assert first["items"][0]["current"] == 0
    assert first["items"][0]["total"] == 58

    registry.advance(task_id, 7, message="Processed entry 7")
    active = registry.snapshot()["items"][0]
    assert active["current"] == 7
    assert active["total"] == 58
    assert active["state"] == "running"

    registry.complete(task_id)
    done = registry.snapshot()
    assert done["running"] == 0
    assert done["items"][0]["current"] == 58
    assert done["items"][0]["state"] == "completed"


def test_cancellable_task_moves_to_failed_when_cancelled():
    registry = TaskRegistry()
    task_id = registry.create("Long scan", total=10)
    called = []
    registry.bind_canceller(task_id, lambda: called.append(True))
    active = registry.snapshot()["items"][0]
    assert active["cancellable"] is True
    assert registry.request_cancel(task_id) is True
    snap = registry.snapshot()
    assert snap["running"] == 0
    assert snap["failed"] == 1
    row = snap["items"][0]
    assert row["state"] == "failed"
    assert "Cancelled by user" in row["error"]
    assert called == [True]


def test_clear_history_preserves_running_tasks():
    registry = TaskRegistry()
    running_id = registry.create("Still running", total=5)
    completed_id = registry.create("Done")
    failed_id = registry.create("Failed")
    registry.complete(completed_id)
    registry.fail(failed_id, "boom")

    result = registry.clear_history()

    assert result == {"cleared": 2, "completed": 1, "failed": 1}
    snap = registry.snapshot()
    assert snap["running"] == 1
    assert snap["completed"] == 0
    assert snap["failed"] == 0
    assert [row["id"] for row in snap["items"]] == [running_id]
