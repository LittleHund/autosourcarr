from copy import deepcopy
from app.diagnostic_groups import group_diagnostics


def record(chapter=1, source='mangafire', **overrides):
    return dict(id=str(chapter), key=f'source-host-pages:{chapter}', component='Source engine',
                message='Source needs CAPTCHA', error_type='ChapterPagesBlocked', severity='error',
                path=f'/api/v1/chapters/{chapter}/pages', method='GET', status_code=503,
                source_context={'source_id': source, 'source_chapter_id': str(chapter)},
                detail=f'Full original trace {chapter}', count=3, resolved=False, pinned=False,
                first_seen=f'2026-09-13T14:00:{chapter:02d}+00:00',
                last_seen=f'2026-09-13T14:10:{chapter:02d}+00:00') | overrides


def test_groups_same_source_cause_retains_exact_members_and_latest_context():
    original = [record(i) for i in range(1, 15)]
    before = deepcopy(original)
    first = group_diagnostics(original)
    assert len(first) == 1
    assert first[0]['count'] == 42 and first[0]['record_count'] == 14
    assert first[0]['id'] == '14'
    assert first[0]['source_context']['source_chapter_id'] == '14'
    assert first[0]['first_seen'] == original[0]['first_seen']
    assert len(first[0]['member_ids']) == len(first[0]['paths']) == 14
    assert group_diagnostics(original) == first  # polling never increments events
    first[0]['source_context']['source_id'] = 'changed'
    assert original == before  # defensive copy; exports keep all14 full traces


def test_distinct_sources_status_causes_operations_and_unknown_urls_stay_separate():
    rows = [record(), record(2, source='other'), record(3, status_code=429),
            record(4, message='HTTP error 521'), record(5, path='/api/v1/chapters/5'),
            record(6, path='/unknown/6'), record(7, path='/unknown/7')]
    assert len(group_diagnostics(rows)) == 7


def test_one_success_does_not_hide_remaining_failures_or_merge_history():
    rows = [record(1), record(2)]
    rows[0]['resolved'] = True
    groups = group_diagnostics(rows)
    assert len(groups) == 2
    assert [(g['count'], g['record_count']) for g in groups] == [(3, 1), (3, 1)]
    assert sum(not g['resolved'] for g in groups) == 1
    rows[0]['resolved'] = False
    assert group_diagnostics(rows)[0]['count'] == 6


def test_active_pinned_root_precedes_newer_derivative_error():
    root = record(1, key='integrated-source-engine', pinned=True, component='Integrated source engine')
    groups = group_diagnostics([root, record(59)])
    assert groups[0]['pinned'] is True


def test_browser_backend_events_are_not_claimed_as_same_occurrence():
    groups = group_diagnostics([record(1), record(2, client_side=True)])
    assert len(groups) == 2


def test_import_source_fallback_key_prevents_merging_distinct_sources():
    groups = group_diagnostics([record(1, key='import-source:one', source_context={}, path=''),
                               record(2, key='import-source:two', source_context={}, path='')])
    assert len(groups) == 2
