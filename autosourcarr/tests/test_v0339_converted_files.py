"""Native POSIX scratch-reader gates; never substitute unavailable dir-fd checks."""
import importlib
import os
from pathlib import Path

import pytest

pytestmark = pytest.mark.skipif(os.name != 'posix', reason='Actual POSIX directory descriptors and no-follow semantics require Linux')


@pytest.fixture
def reader(monkeypatch):
    monkeypatch.syspath_prepend(str(Path(__file__).resolve().parents[2]/'repair-worker'))
    return importlib.import_module('internal_runner').read_converted_jar


def test_reads_only_the_named_regular_file_inside_its_scratch_profile(reader, tmp_path):
    folder = tmp_path/'extensions'; folder.mkdir()
    data = b'OWNED_CONVERTED_BYTES' * 20
    (folder/'candidate.jar').write_bytes(data)
    assert reader(tmp_path, {'apkName': 'candidate.apk'}) == data
    with pytest.raises(ValueError): reader(tmp_path, {'apkName': '../../outside.apk'})


def test_symlinked_converted_file_and_directory_are_never_followed(reader, tmp_path):
    profile = tmp_path/'profile'; profile.mkdir()
    folder = profile/'extensions'; folder.mkdir()
    outside = tmp_path/'outside-converted.jar'; outside.write_bytes(b'PRIVATE_OUTSIDE' * 20)
    (folder/'candidate.jar').symlink_to(outside)
    with pytest.raises(OSError): reader(profile, {'apkName': 'candidate.apk'})
    (folder/'candidate.jar').unlink(); folder.rmdir()
    folder.symlink_to(tmp_path, target_is_directory=True)
    with pytest.raises(OSError): reader(profile, {'apkName': 'candidate.apk'})


def test_hardlinked_and_oversized_converted_files_are_rejected(reader, tmp_path):
    folder = tmp_path/'extensions'; folder.mkdir()
    target = folder/'candidate.jar'; target.write_bytes(b'CONVERTED' * 30)
    os.link(target, tmp_path/'extra-link')
    with pytest.raises(ValueError): reader(tmp_path, {'apkName': 'candidate.apk'})
    (tmp_path/'extra-link').unlink()
    with target.open('wb') as output: output.truncate(32*1024*1024 + 1)
    with pytest.raises(ValueError): reader(tmp_path, {'apkName': 'candidate.apk'})
