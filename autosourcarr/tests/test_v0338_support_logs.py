"""The download keeps technical resource facts while excluding private extras."""
import json

from app.support_logs import SupportLogStore


def test_account_resource_snapshot_is_exact_and_allowlisted_in_downloadable_logs():
    store = SupportLogStore()
    store.observe_control({'state': 'failed', 'action': 'login', 'busy': False,
        'device_code': 'ABCD-EFGH',
        'diagnostics': {'reason': 'resource-limit', 'phase': 'starting',
            'exit_code': 70, 'http_status': None, 'code_received': False,
            'launcher_stage': 'exec', 'os_error': 11,
            'resources': {'nproc_soft': '9007199254740993', 'nproc_hard': 'unlimited',
                'cgroup_pids_current': '0', 'cgroup_pids_max': '512',
                'cgroup_scope': 'visible-v2-only', 'ancestor_limits': 'unknown',
                'open_files_soft': True, 'open_files_hard': 1024,
                'cgroup_memory_max_bytes': '18446744073709551616',
                'address_space_soft_bytes': '/private/DO_NOT_EXPORT',
                'private_password': 'DO_NOT_EXPORT'}}})
    report = store.snapshot()
    expected = {'nproc_soft': '9007199254740993', 'nproc_hard': 'unlimited',
        'cgroup_pids_current': '0', 'cgroup_pids_max': '512',
        'cgroup_scope': 'visible-v2-only', 'ancestor_limits': 'unknown'}
    assert report['repair_account']['diagnostics']['resources'] == expected
    observed = json.loads(report['entries'][0]['message'])
    assert observed['diagnostics']['resources'] == expected
    encoded = json.dumps(report)
    assert 'DO_NOT_EXPORT' not in encoded and 'ABCD-EFGH' not in encoded


def test_malformed_resource_container_does_not_replace_the_original_account_error():
    for resources in (None, [], 'private value', {'nproc_soft': '12\n'}):
        store = SupportLogStore()
        store.observe_control({'state': 'failed', 'action': 'login',
            'diagnostics': {'reason': 'resource-limit', 'phase': 'starting',
                            'code_received': False, 'os_error': 11, 'resources': resources}})
        details = store.snapshot()['repair_account']['diagnostics']
        assert details['reason'] == 'resource-limit' and details['os_error'] == 11
        assert 'resources' not in details
