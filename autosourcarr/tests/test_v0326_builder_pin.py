"""First, retained and explicit-update revisions use controlled Git/SDK fixtures."""
import json
from pathlib import Path
import shutil

import pytest

from test_v039_builder import sdk, run_build

PIN='e40e1b78f46baf5ef9dc68cb980f82c66766a211'


def test_first_build_uses_shipped_pin_without_resolving_main(sdk):
    work, _, _=sdk
    lock=work/'extension/keiyoushi-commit.lock'
    assert not lock.exists()
    events=work/'git-events.jsonl'
    result=run_build(sdk, KEI_GIT_EVENTS=str(events))
    assert result.returncode==0,result.stdout+result.stderr
    assert lock.read_text().strip()==PIN
    commands=[json.loads(line) for line in events.read_text().splitlines()]
    assert ['reset','--hard',PIN] in commands
    assert not any('main' in command or 'origin/main' in command for command in commands)


def test_retained_revision_survives_a_new_release_default(sdk):
    work, _, _=sdk
    retained='a'*40
    (work/'extension/keiyoushi-commit.lock').write_text(retained+'\n')
    result=run_build(sdk)
    assert result.returncode==0,result.stdout+result.stderr
    assert (work/'extension/keiyoushi-commit.lock').read_text().strip()==retained


def test_explicit_update_relocks_the_selected_full_revision(sdk):
    work, _, _=sdk
    latest='c'*40
    result=run_build(sdk, KEIYOUSHI_UPDATE='1', FAKE_KEI_LATEST=latest)
    assert result.returncode==0,result.stdout+result.stderr
    assert (work/'extension/keiyoushi-commit.lock').read_text().strip()==latest


@pytest.mark.parametrize('failure', ['missing-default', 'invalid-default', 'invalid-retained', 'wrong-origin'])
def test_unreviewed_or_wrong_origin_inputs_fail_before_compilation(sdk, failure):
    work, _, env=sdk
    options={}
    if failure=='missing-default': (work/'extension/keiyoushi-default.lock').unlink()
    elif failure=='invalid-default': (work/'extension/keiyoushi-default.lock').write_text('main\n')
    elif failure=='invalid-retained': (work/'extension/keiyoushi-commit.lock').write_text('invalid\n')
    else: options['FAKE_KEI_ORIGIN']='https://github.com/unrelated/project.git'
    result=run_build(sdk, **options)
    assert result.returncode==27,result.stdout+result.stderr
    assert not Path(env['EVENTS']).exists()


def test_new_checkout_is_initialized_for_reviewed_origin(sdk):
    work, _, _=sdk
    # Keep the fixture gradlew/compiler but make the coordinator initialize Git.
    shutil.rmtree(work/'extension/.build/extensions-source/.git')
    events=work/'git-events.jsonl'
    result=run_build(sdk, KEI_GIT_EVENTS=str(events))
    assert result.returncode==0,result.stdout+result.stderr
    commands=[json.loads(line) for line in events.read_text().splitlines()]
    assert ['remote','add','origin','https://github.com/keiyoushi/extensions-source.git'] in commands
    assert (work/'extension/keiyoushi-commit.lock').read_text().strip()==PIN
