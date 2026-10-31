"""Contract derived independently from pinned Suwayomi Kotlin types, not a live JVM."""
import json
from pathlib import Path
import re

import pytest
import httpx
from graphql import GraphQLError, build_schema, graphql_sync
from graphql.language.ast import StringValueNode
from app.source_challenges import configure_source_captcha
from app.suwayomi import SuwayomiClient

HERE = Path(__file__).parent / 'fixtures'
SID = '6084907896154116083'

def schema_and_state():
    schema = build_schema((HERE / 'suwayomi-source-settings-v2.3.2243.graphql').read_text())
    def parse_long_string(value):
        if not isinstance(value, str) or not re.fullmatch(r'[+-]?\d+', value):
            raise GraphQLError('LongString requires a decimal string')
        number = int(value)
        if not -(2**63) <= number < 2**63:
            raise GraphQLError('LongString value is outside signed 64-bit range')
        return value
    def parse_literal(node, variables=None):
        if not isinstance(node, StringValueNode):
            raise GraphQLError('LongString requires a quoted literal')
        return parse_long_string(node.value)
    scalar = schema.type_map['LongString']
    scalar.parse_value = parse_long_string
    scalar.parse_literal = parse_literal
    scalar.serialize = parse_long_string
    prefs = [{'__typename': 'ListPreference', 'key': 'lang'},
             {'__typename': 'SwitchPreference', 'key': 'unrelated', 'title': 'Other', 'enabled': True, 'currentValue': False, 'default': False},
             {'__typename': 'SwitchPreference', 'key': 'solve_captcha', 'title': 'Solve', 'enabled': True, 'currentValue': False, 'default': False}]
    mutations = []
    def source(_, info, id):
        assert id == SID
        return {'id': id, 'preferences': prefs}
    def mutate(_, info, input):
        assert input == {'source': SID, 'change': {'position': 2, 'switchState': True}}
        mutations.append(input)
        prefs[input['change']['position']]['currentValue'] = input['change']['switchState']
        return {'preferences': prefs, 'source': {'id': SID, 'preferences': prefs}}
    schema.type_map['Query'].fields['source'].resolve = source
    schema.type_map['Mutation'].fields['updateSourcePreference'].resolve = mutate
    return schema, prefs, mutations

@pytest.mark.asyncio
async def test_captcha_operations_validate_against_pinned_schema():
    schema, prefs, mutations = schema_and_state()
    operations = []
    errors = []
    def upstream(request):
        operation = json.loads(request.content)
        query = operation['query']
        operations.append(query)
        response = graphql_sync(schema, query, variable_values=operation['variables'])
        if response.errors:
            errors.extend(e.message for e in response.errors)
        return httpx.Response(200, json=response.formatted)
    client = SuwayomiClient('http://contract.invalid')
    await client.close()
    client.client = httpx.AsyncClient(transport=httpx.MockTransport(upstream))
    try:
        result = await configure_source_captcha(client, [{'id': SID, 'name': 'MangaFire'}])
        assert result[0]['state'] == 'enabled', (result, errors)
        assert len(mutations) == 1
        assert len(operations) == 3
        assert prefs[1]['currentValue'] is False
        result2 = await configure_source_captcha(client, [{'id': SID, 'name': 'MangaFire'}])
        assert result2[0]['state'] == 'enabled' and not result2[0]['changed']
        assert len(mutations) == 1 and len(operations) == 4
    finally:
        await client.close()


def test_pinned_scalar_rejects_truncated_or_invalid_source_ids():
    schema, _, _ = schema_and_state()
    query = 'query ($id:LongString!) { source(id:$id) { id } }'
    for value in [int(SID), str(2**63), 'not-a-number']:
        result = graphql_sync(schema, query, variable_values={'id': value})
        assert result.errors, value
    result = graphql_sync(schema, query, variable_values={'id': SID})
    assert result.data == {'source': {'id': SID}}, result
    for mutation in [
        'mutation ($input:UpdateSourcePreferenceInput!) { updateSourcePreference(input:$input) { preferences { badField } } }',
        'mutation ($input:WrongSourcePreferenceInput!) { updateSourcePreference(input:$input) { preferences { __typename } } }',
    ]:
        assert graphql_sync(schema, mutation, variable_values={'input': {'source': SID, 'change': {'position': 2, 'switchState': True}}}).errors
    print('Coercion and schema-negative controls passed')

