using System.Text.Json.Nodes;
using AutoSourcarr.Core.Catalogue.Chapters;
using AutoSourcarr.Core.Matching.Decisions;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Reader;

namespace AutoSourcarr.Tests.Catalogue.Chapters;

/// <summary>CHAP-1..CHAP-4: chapter numbers from any numbering word, parts in order, seasons keep counting, one source per
/// chapter with groups serving their member with the most chapters, and candidate rejection/restore.</summary>
public static class ChapterNumberingTests
{
    /// <summary>Real-world chapter labels (from Mihon sources) without a source number → the number they are filed under (-1 = by title).</summary>
    private static readonly (string Name, double Number)[] Labels =
    [
        ("Chapter 12", 12), ("Ch. 12.5", 12.5), ("Ch.7", 7), ("Chapter 10 Part 1", 10.1), ("Chapter 10 Part 2", 10.2),
        ("Chapter 10 (Part 2)", 10.2), ("Ch. 10 Pt. 3", 10.3), ("Chapter 10a", 10.1), ("Chapter 10b - The End", 10.2),
        ("Chapter 05 Pt 10", 5.91), ("Vol.3 Ch.25", 25), ("Vol. 2 Chapter 14: Return", 14), ("Vol.2 Ch.10.5", 10.5), ("Volume 4", 4),
        ("Episode 33", 33), ("Ep. 7 - Night", 7), ("Ep.08", 8), ("E12", 12), ("c045", 45), ("Act 4", 4), ("Stage 10", 10),
        ("No. 7", 7), ("#105", 105), ("Number 3", 3), ("Part 4", 4), ("Part 4 Chapter 10", 10), ("Lesson 21", 21), ("Round 112", 112),
        ("第12話", 12), ("第3巻 第18話", 18), ("12話", 12), ("제45화", 45), ("第100章", 100), ("Episode 3 前編", 3.1), ("第5話 後編", 5.2),
        ("Capítulo 15", 15), ("Chapitre 8", 8), ("Kapitel 3", 3), ("Season 2 Episode 5", 5), ("S2 Ep. 1 - Log In", 1),
        ("(S2) Ep. 1 - Log In (1) (Season 2 Premiere)", 1), ("[Season 3] Chapter 7", 7), ("Chapter 5 - Boss's 2 Friends", 5),
        ("Chapter 2.5", 2.5), ("Ch. 12", 12), ("Chapter １２.５", 12.5), ("Episode -2", -1), ("Special", -1), ("Oneshot", -1),
        ("Prologue", -1), ("Chapter ²", -1),
    ];

    public static void Register(TestSuite suite)
    {
        suite.Add("chapters: numbering words table (Chapter, Ep., Act, Stage, No., Part, Vol., 第…話 …)", () =>
        {
            Check.True(Labels.Length >= 40, "at least 40 real-world names");
            foreach (var (name, number) in Labels)
            {
                var (key, actual) = ChapterNumbers.Key(-1, name);
                Check.True(Math.Abs(actual - number) < 1e-9, name + " → " + actual.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Check.True(number < 0 ? key.StartsWith("t:", StringComparison.Ordinal) : key == "n:" + ChapterNumbers.Format(number), name + " key " + key);
            }
        });

        suite.Add("chapters: a source number is refined only by an explicit part of that same number", () =>
        {
            Check.Equal(10.2, ChapterNumbers.Key(10, "Chapter 10 Part 2").Number);
            Check.Equal(10.1, ChapterNumbers.Key(10, "Ch. 10a").Number);
            Check.Equal(10.0, ChapterNumbers.Key(10, "Part 4 Chapter 10").Number); // an arc named first is not a part
            Check.Equal(12.0, ChapterNumbers.Key(12, "Chapter 10 Part 2").Number); // another number: the source wins
            Check.Equal(3.0, ChapterNumbers.Key(3, "Chapter 3").Number);
            Check.Equal("n:10.2", ChapterNumbers.Key(10, "Chapter 10 Part 2").Key);
        });

        suite.Add("chapters: part 1 always precedes part 2 within the same number", () =>
        {
            string[] shuffled = ["Chapter 11", "Chapter 10 Part 2", "Special", "Chapter 10", "Chapter 10.5", "Chapter 10 Part 1", "Chapter 9", "Chapter 10 Part 12"];
            var ordered = shuffled.Select(name => (Name: name, Number: ChapterNumbers.Key(-1, name).Number))
                .Order(Comparer<(string Name, double Number)>.Create((left, right) => ChapterNumbering.Compare(left.Number, right.Number))).Select(item => item.Name).ToArray();
            Check.Equal("Chapter 9|Chapter 10|Chapter 10 Part 1|Chapter 10 Part 2|Chapter 10.5|Chapter 10 Part 12|Chapter 11|Special", string.Join('|', ordered));
            Check.True(ChapterNumbering.WithPart(10, 9) < ChapterNumbering.WithPart(10, 10) && ChapterNumbering.WithPart(10, 19) < 11, "late parts stay inside the chapter");
        });

        suite.Add("chapters: seasons keep counting from the previous season", () =>
        {
            var chapters = Enumerable.Range(1, 40).Select(n => ((double)n, "Ep. " + n)).Concat(Enumerable.Range(1, 30).Select(n => ((double)n, "S2 Ep. " + n + " - Title " + n)))
                .Concat(Enumerable.Range(1, 5).Select(n => ((double)n, "Season 3 Episode " + n))).ToArray();
            var plan = SeasonContinuation.Plan(chapters);
            Check.True(plan.Continues);
            Check.Equal(41.0, plan.Apply(1, "S2 Ep. 1 - Log In"));
            Check.Equal(70.0, plan.Apply(30, "S2 Ep. 30"));
            Check.Equal(71.0, plan.Apply(1, "Season 3 Episode 1"));
            Check.Equal(7.0, plan.Apply(7, "Ep. 7"));
            Check.Equal("Chapter 41: S2 Ch.1: Log In", ChapterNumbering.DisplayName(41, "S2 Ep. 1 - Log In"));
            Check.Equal("Chapter 71: S3 Ch.1", ChapterNumbering.DisplayName(71, "Season 3 Episode 1"));
            Check.Equal("Chapter 10 Part 2: The Fall", ChapterNumbering.DisplayName(10.2, "Chapter 10 Part 2: The Fall"));
            // Absolute numbering across seasons is left alone; a season whose previous season is missing keeps its numbers.
            Check.True(!SeasonContinuation.Plan([(41, "S2 Ep. 41"), (40, "Ep. 40"), (1, "Ep. 1")]).Continues);
            Check.True(!SeasonContinuation.Plan([(1, "S2 Ep. 1"), (2, "S2 Ep. 2")]).Continues);
            // Released naming stays for chapters that are not continued (reader-serving oracle).
            Check.Equal(ChapterResolver.DisplayName(1, "(S2) Ep. 1 - Log In (1) (Season 2 Premiere)"), ChapterNumbering.DisplayName(1, "(S2) Ep. 1 - Log In (1) (Season 2 Premiere)"));
        });

        suite.Add("chapters: a restarted season is stored after season 1 and listed as Chapter 41: S2 Ch.1", () =>
        {
            using var world = new ChapterWorld();
            var input = Enumerable.Range(1, 40).Select(n => ChapterWorld.Chapter(n, n, "Episode " + n))
                .Concat(Enumerable.Range(1, 3).Select(n => ChapterWorld.Chapter(100 + n, n, "S2 Ep. " + n + " - Arc " + n))).ToArray();
            world.Write(ChapterWorld.SourceA, ChapterWorld.MangaA, input);
            Check.Equal(43L, world.Scalar(DatabaseFile.Origins, "SELECT COUNT(*) FROM virtual_chapters WHERE entry_id=1"));
            Check.Equal(43L, world.Scalar(DatabaseFile.Origins, "SELECT MAX(chapter_number) FROM virtual_chapters WHERE entry_id=1"));
            var list = world.Reader.ListChapters(ChapterWorld.Entry);
            var first = list.Single(row => ReaderValue.Number(row, "chapter_number") == 41);
            Check.Equal("Chapter 41: S2 Ch.1: Arc 1", ReaderValue.Text(first, "display_name"));
            Check.Equal("Chapter 40", ReaderValue.Text(list.Single(row => ReaderValue.Number(row, "chapter_number") == 40), "display_name"));
            // Every chapter stays its own chapter with one serving source.
            Check.True(list.All(row => ReaderValue.Integer(row, "candidate_count") == 1));
        });

        suite.Add("chapters: parts of one chapter stay separate chapters in order", () =>
        {
            using var world = new ChapterWorld();
            world.Write(ChapterWorld.SourceA, ChapterWorld.MangaA, ChapterWorld.Chapter(1, 10, "Chapter 10 Part 1"), ChapterWorld.Chapter(2, 10, "Chapter 10 Part 2"), ChapterWorld.Chapter(3, 11, "Chapter 11"));
            var numbers = world.Reader.ListChapters(ChapterWorld.Entry).Select(row => ReaderValue.Number(row, "chapter_number")).ToArray();
            Check.Equal("11|10.2|10.1", string.Join('|', numbers.Select(n => n.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        });

        suite.Add("chapters: a source group shares one priority and its member with the most chapters serves", () =>
        {
            // Pure rule: member B scores higher, A has more chapters → A serves; the outside source keeps its place.
            var rows = new List<JsonObject>
            {
                new() { ["id"] = 2, ["source_id"] = "B", ["resolver_score"] = 130.0 },
                new() { ["id"] = 3, ["source_id"] = "C", ["resolver_score"] = 120.0 },
                new() { ["id"] = 1, ["source_id"] = "A", ["resolver_score"] = 110.0 },
            };
            ServingGroups.Apply(rows, new Dictionary<string, long> { ["A"] = 7, ["B"] = 7 }, new Dictionary<string, long> { ["A"] = 12, ["B"] = 3 });
            Check.Equal("1|2|3", string.Join('|', rows.Select(row => ReaderValue.Integer(row, "id"))));
            Check.True(ReaderValue.Flag(rows[0], "source_group_winner"));

            using var world = new ChapterWorld();
            world.Database.Write(DatabaseFile.Catalog, session => session.Execute("""
                INSERT INTO source_groups(id,name,priority,created_at,updated_at) VALUES(7,'Scan family',10,'now','now');
                UPDATE source_config SET group_id=7,priority=10;
                """));
            // A has three chapters; B has only chapter 1 but with more pages (which would win on its own).
            world.Write(ChapterWorld.SourceA, ChapterWorld.MangaA, ChapterWorld.Chapter(1, 1, "Chapter 1", 10), ChapterWorld.Chapter(2, 2, "Chapter 2", 10), ChapterWorld.Chapter(3, 3, "Chapter 3", 10));
            world.Write(ChapterWorld.SourceB, ChapterWorld.MangaB, ChapterWorld.Chapter(11, 1, "Chapter 1", 40));
            var chapter = world.Reader.ListChapters(ChapterWorld.Entry).Single(row => ReaderValue.Number(row, "chapter_number") == 1);
            Check.Equal(ChapterWorld.SourceA, ReaderValue.Text(chapter, "serving_source_id"));
            Check.Equal(2L, ReaderValue.Integer(chapter, "candidate_count"));
            var detail = world.Reader.ChapterDetails(ReaderValue.Integer(chapter, "id"));
            Check.Equal(ReaderValue.Integer(chapter, "serving_candidate_id"), detail["would_serve_candidate_id"]!.GetValue<long>());
        });

        suite.Add("chapters: rejecting and restoring a candidate records the caller and survives refreshes", () =>
        {
            using var world = new ChapterWorld();
            world.Write(ChapterWorld.SourceA, ChapterWorld.MangaA, ChapterWorld.Chapter(1, 1, "Chapter 1", 10));
            world.Write(ChapterWorld.SourceB, ChapterWorld.MangaB, ChapterWorld.Chapter(11, 1, "Chapter 1", 10));
            var review = new ChapterCandidateReview(world.Database, world.Gate, world.Reader.ChapterDetails, world.Reader.ReconcileVisibility, world.Log);
            var candidate = world.Scalar(DatabaseFile.Origins, "SELECT id FROM chapter_candidates WHERE source_id=$source", ("$source", ChapterWorld.SourceB));
            var detail = review.Reject(candidate, DecisionActor.User("alice"));
            Check.Equal(1, detail["candidates"]!.AsArray().Count);
            Check.Equal(1, detail["rejected_candidates"]!.AsArray().Count);
            world.Write(ChapterWorld.SourceB, ChapterWorld.MangaB, ChapterWorld.Chapter(11, 1, "Chapter 1", 12)); // a refresh keeps the rejection
            Check.Equal(0L, world.Scalar(DatabaseFile.Origins, "SELECT active FROM chapter_candidates WHERE id=$id", ("$id", candidate)));
            review.Restore(candidate, DecisionActor.User("alice"));
            Check.Equal(1L, world.Scalar(DatabaseFile.Origins, "SELECT active FROM chapter_candidates WHERE id=$id", ("$id", candidate)));
            Check.Equal("chapter.candidate.reject|chapter.candidate.restore", string.Join('|', world.Log.Kinds));
            Check.True(world.Log.Entries.All(entry => entry.Actor.Name == "alice"));
            Check.Throws<KeyNotFoundException>(() => review.Reject(999999, DecisionActor.User("alice")));
        });
    }
}
