"""Use the exact shipped builder contract without exposing reader credentials."""
from functools import lru_cache
import importlib.util
import os
from pathlib import Path

READER_KEYS = ('AUTOSOURCARR_INTERNAL_URL', 'AUTOSOURCARR_PUBLIC_URL', 'AUTOSOURCARR_READER_API_TOKEN')


@lru_cache(maxsize=1)
def _contract():
    app = Path(__file__).resolve()
    candidates = (
        Path('/opt/autosourcarr-tools/build/extension/reader_build_config.py'),
        app.parents[2]/'extension/reader_build_config.py',
        app.parents[1]/'bundled/build/extension/reader_build_config.py',
    )
    for path in candidates:
        if path.is_file():
            spec = importlib.util.spec_from_file_location('autosourcarr_reader_build_contract', path)
            module = importlib.util.module_from_spec(spec)
            spec.loader.exec_module(module)
            return module
    return None


def evaluate_reader_config(values):
    contract = _contract()
    if contract is None:
        return {'ready': False, 'exit_code': 24, 'warnings': [], 'errors': [
            {'key': 'builder', 'code': 24, 'message': 'Reader validation tools are missing. Rebuild the AutoSourcarr image.'}]}
    # The integrated coordinator forwards this explicit compatibility override.
    # Docker-only Android addresses remain disallowed by that coordinator.
    return contract.evaluate(values, allow_insecure=os.environ.get('AUTOSOURCARR_ALLOW_INSECURE_READER_API') == '1')


def live_reader_config(settings):
    return evaluate_reader_config({key: getattr(settings, key.lower()) for key in READER_KEYS})


def reader_config_message(result):
    return ' '.join(item['message'] for item in result['errors']) + ' Open setup → Reader extensions to correct the saved settings, then restart this instance before building.'
