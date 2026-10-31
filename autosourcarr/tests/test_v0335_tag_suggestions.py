"""Bounded suggestion selection preserves the established complete ranking."""
import pytest

from app import tag_store
from test_v0326_catalog_performance import tags, insert_tags, eager_suggestions
from test_v087_recovery import env


@pytest.mark.parametrize('limit', [1, 7, 100, 200, 999, -1])
def test_suggestion_top_k_matches_full_stable_ranking(tags, limit):
    _, database = tags
    ids = insert_tags(database, [(namespace, f'Unusual spelling {i:03}')
                                for namespace in ('artist', 'female', 'tag') for i in range(30)])
    tag_store.review(ids[('tag', 'Unusual spelling 000')], ids[('tag', 'Unusual spelling 001')], 'same')
    tag_store.review(ids[('tag', 'Unusual spelling 002')], ids[('tag', 'Unusual spelling 003')], 'different')
    expected = eager_suggestions(database, limit)
    actual = tag_store.suggestions(limit)
    assert actual == expected
    assert len(actual['items']) == max(1, min(limit, 200))
