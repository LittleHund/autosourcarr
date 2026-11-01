using System.Globalization;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Matching.Decisions;
using AutoSourcarr.Core.Persistence;

namespace AutoSourcarr.Core.Catalogue.Chapters;

/// <summary>
/// Rejecting and restoring one chapter candidate (0.4.5 reject_chapter_candidate / restore_chapter_candidate). A rejection
/// is a fact about the source chapter (chapter_candidate_rejections): it survives refreshes and is lifted only by a restore.
/// Rejecting the served candidate forgets the served choice so the next read picks again; origin history is kept.
/// </summary>
public sealed class ChapterCandidateReview(SqliteDatabase database, object sourceWriteGate, Func<long, JsonObject> chapterDetails,
    Action<long> reconcileVisibility, IDecisionLog decisions, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public JsonObject Reject(long candidateId, DecisionActor actor) => Change(candidateId, actor, reject: true);
    public JsonObject Restore(long candidateId, DecisionActor actor) => Change(candidateId, actor, reject: false);

    private JsonObject Change(long candidateId, DecisionActor actor, bool reject)
    {
        ArgumentNullException.ThrowIfNull(actor);
        lock (sourceWriteGate)
        {
            var stamp = _clock.GetUtcNow().ToString("yyyy-MM-dd'T'HH:mm:ss.ffffffzzz", CultureInfo.InvariantCulture);
            var candidate = database.Write(DatabaseFile.Origins, session =>
            {
                var row = session.Query("""
                    SELECT cc.*,vc.entry_id FROM chapter_candidates cc JOIN virtual_chapters vc ON vc.id=cc.virtual_chapter_id WHERE cc.id=$id
                    """, ("$id", candidateId)).FirstOrDefault() ?? throw new KeyNotFoundException("Chapter candidate not found");
                if (reject)
                {
                    session.Execute("""
                        INSERT INTO chapter_candidate_rejections(source_id,source_chapter_id,source_manga_id,source_name,name,rejected_at)
                        VALUES($source,$chapter,$manga,$name,$label,$time)
                        ON CONFLICT(source_id,source_chapter_id) DO UPDATE SET source_manga_id=excluded.source_manga_id,source_name=excluded.source_name,
                          name=excluded.name,rejected_at=excluded.rejected_at
                        """, ("$source", row.Text("source_id")), ("$chapter", row.Integer("source_chapter_id")), ("$manga", row.Integer("source_manga_id")),
                        ("$name", row.Text("source_name")), ("$label", row.Text("name")), ("$time", stamp));
                    session.Execute("UPDATE chapter_candidates SET active=0 WHERE id=$id", ("$id", candidateId));
                    session.Execute("DELETE FROM served_origin WHERE virtual_chapter_id=$chapter AND candidate_id=$id", ("$chapter", row.Integer("virtual_chapter_id")), ("$id", candidateId));
                }
                else
                {
                    session.Execute("DELETE FROM chapter_candidate_rejections WHERE source_id=$source AND source_chapter_id=$chapter",
                        ("$source", row.Text("source_id")), ("$chapter", row.Integer("source_chapter_id")));
                    session.Execute("UPDATE chapter_candidates SET active=1,last_seen=$time WHERE id=$id", ("$time", stamp), ("$id", candidateId));
                }
                return row;
            });
            var chapterId = candidate.Integer("virtual_chapter_id");
            reconcileVisibility(candidate.Integer("entry_id"));
            decisions.Record(new DecisionEvent(reject ? "chapter.candidate.reject" : "chapter.candidate.restore", actor, [candidate.Integer("entry_id")],
                [candidate.Text("source_id") + ":" + candidate.Integer("source_manga_id").ToString(CultureInfo.InvariantCulture)],
                (reject ? "Rejected" : "Restored") + " source chapter " + candidate.Integer("source_chapter_id").ToString(CultureInfo.InvariantCulture) + " for chapter " + chapterId.ToString(CultureInfo.InvariantCulture),
                new JsonObject { ["candidate_id"] = candidateId, ["active"] = candidate.Flag("active") },
                new JsonObject { ["candidate_id"] = candidateId, ["active"] = !reject }));
            return chapterDetails(chapterId);
        }
    }
}
