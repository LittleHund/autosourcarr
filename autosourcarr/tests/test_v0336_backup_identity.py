import pytest

from app.backup_import import BackupDecodeError,_parse_backup_manga


@pytest.mark.parametrize('raw',[b'/work/\xff',b'/work/\xfe'])
def test_malformed_backup_url_is_rejected_instead_of_replacement_identity(raw):
    with pytest.raises(BackupDecodeError,match='UTF-8'):
        _parse_backup_manga(b'\x12'+bytes([len(raw)])+raw)


def test_multilingual_backup_identity_and_display_text_are_retained():
    url='/work/東京'.encode()
    result=_parse_backup_manga(b'\x12'+bytes([len(url)])+url+b'\x1a\x04Caf\xff')
    assert result['url']=='/work/東京'
    assert result['title']=='Caf\ufffd'
