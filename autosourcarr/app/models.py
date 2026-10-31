from typing import Any, Literal
from pydantic import BaseModel, Field


class SourceUpdate(BaseModel):
    enabled_language: bool | None = None
    enabled_search: bool | None = None
    enabled_chapters: bool | None = None
    priority: int | None = Field(None, ge=1, le=100000)
    group_id: int | None = Field(None, ge=1)


class SourceGroupCreate(BaseModel):
    name: str = Field(min_length=1, max_length=120)
    priority: int = Field(1000, ge=1, le=100000)


class SourceGroupUpdate(BaseModel):
    name: str | None = Field(None, min_length=1, max_length=120)
    priority: int | None = Field(None, ge=1, le=100000)


class ManualMatchRequest(BaseModel):
    source_id: str
    source_manga_id: int
    confidence: float = 1.0
    mode: Literal["replace", "add"] = "replace"
    # The manual-search card already contains enough metadata to persist the
    # selection locally.  Supplying it lets the API acknowledge the user's
    # click immediately while full details/chapters hydrate in the background.
    candidate: dict[str, Any] | None = None
    defer_hydration: bool = False


class ManualGroupMemberRequest(BaseModel):
    source_id: str
    source_manga_id: int


class SeparateSourceRequest(BaseModel):
    source_id: str
    source_manga_id: int
    candidate: dict[str, Any] | None = None
    defer_hydration: bool = True


class DuplicateReviewRequest(BaseModel):
    left_entry_id: int
    right_entry_id: int
    decision: Literal["duplicate", "not_duplicate", "clear"]


class DuplicateRecordRef(BaseModel):
    source_id: str
    source_manga_id: int


class DuplicateUnmatchRequest(BaseModel):
    records: list[DuplicateRecordRef] = Field(min_length=1)


class PreferredTitleRequest(BaseModel):
    title: str


class AliasRequest(BaseModel):
    title: str
    language: str = ""


class SettingsUpdate(BaseModel):
    auto_match_threshold: float | None = Field(None, ge=0, le=1)
    metadata_ttl_seconds: int | None = Field(None, ge=60)


class EnvFileUpdate(BaseModel):
    revision: str | None = None
    values: dict[str, str] = Field(default_factory=dict)


class DatabaseMoveRequest(BaseModel):
    path: str = Field(min_length=1, max_length=4096)


class QualityRequest(BaseModel):
    sample_count: int = Field(5, ge=2, le=12)


class ExtensionActionRequest(BaseModel):
    action: Literal["install", "update", "uninstall"]


class UiPreferencesUpdate(BaseModel):
    values: dict[str, Any] = Field(default_factory=dict)


class RepositoryPreviewRequest(BaseModel):
    url: str = Field(min_length=1, max_length=2048)


class RepositoryChangeRequest(RepositoryPreviewRequest):
    revision: str = Field(min_length=64, max_length=64)


class MixedEntryReviewRequest(BaseModel):
    fingerprint: str = Field(pattern=r"^[0-9a-f]{64}$")
    decision: Literal["keep", "clear"]
