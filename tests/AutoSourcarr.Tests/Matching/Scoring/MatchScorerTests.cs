using System.Text.Json.Nodes;
using AutoSourcarr.Core.Matching;
using AutoSourcarr.Core.Matching.Scoring;

namespace AutoSourcarr.Tests.Matching;

/// <summary>Phase 8A scorer: MAT-1 floor and missing-word penalty, MAT-2 identity evidence, MAT-3 editions, profiles.</summary>
public static class MatchScorerTests
{
    /// <summary>Public, well-known title pairs. same=true: one work written differently; same=false: sequels, spin-offs, editions, other works.</summary>
    internal const string KnownPairs = """
{"pairs":[
{"a":{"titles":["Attack on Titan"]},"b":{"titles":["ATTACK ON TITAN"]},"same":true},
{"a":{"titles":["Shingeki no Kyojin"]},"b":{"titles":["Shingeki no Kyoujin"]},"same":true},
{"a":{"titles":["Re:Zero - Starting Life in Another World"]},"b":{"titles":["Re:ZERO -Starting Life in Another World-"]},"same":true},
{"a":{"titles":["Kaguya-sama: Love Is War"]},"b":{"titles":["Kaguya-sama - Love is War"]},"same":true},
{"a":{"titles":["JoJo's Bizarre Adventure"]},"b":{"titles":["JoJo’s Bizarre Adventure"]},"same":true},
{"a":{"titles":["Fullmetal Alchemist"],"authors":["Hiromu Arakawa"]},"b":{"titles":["Full Metal Alchemist"],"authors":["Arakawa Hiromu"]},"same":true},
{"a":{"titles":["Spy x Family"],"authors":["Tatsuya Endo"]},"b":{"titles":["SPY×FAMILY"],"authors":["Endou Tatsuya"]},"same":true},
{"a":{"titles":["Sono Bisque Doll wa Koi wo Suru"]},"b":{"titles":["Sono Bisque Doll wa Koi o Suru"]},"same":true},
{"a":{"titles":["Boku no Hero Academia","My Hero Academia"]},"b":{"titles":["My Hero Academia"]},"same":true},
{"a":{"titles":["One Punch-Man"],"authors":["ONE","Yusuke Murata"]},"b":{"titles":["Onepunch-Man"],"authors":["Murata Yuusuke"]},"same":true},
{"a":{"titles":["Dr. Stone"],"year":2017},"b":{"titles":["Dr.STONE"],"year":2017},"same":true},
{"a":{"titles":["Chainsaw Man"],"authors":["Fujimoto Tatsuki"]},"b":{"titles":["Chainsaw Man"],"authors":["Tatsuki Fujimoto"]},"same":true},
{"a":{"titles":["【推しの子】","Oshi no Ko"]},"b":{"titles":["Oshi no Ko"],"authors":["Aka Akasaka"]},"same":true},
{"a":{"titles":["【推しの子】","Oshi no Ko"],"year":2020},"b":{"titles":["Oshi no Ko"],"year":2020},"same":true},
{"a":{"titles":["Frieren: Beyond Journey's End"]},"b":{"titles":["Sousou no Frieren","Frieren: Beyond Journey’s End"]},"same":true},
{"a":{"titles":["The Apothecary Diaries"],"year":2017},"b":{"titles":["The Apothecary Diaries"],"year":2018},"same":true},
{"a":{"titles":["Vinland Saga"],"authors":["Makoto Yukimura"]},"b":{"titles":["VINLAND SAGA"],"authors":["Yukimura Makoto"]},"same":true},
{"a":{"titles":["Fire Punch"],"authors":["Tatsuki Fujimoto"]},"b":{"titles":["Fire Punch"],"authors":["Fujimoto Tatsuki"]},"same":true},
{"a":{"titles":["Hunter x Hunter"],"year":1998},"b":{"titles":["HUNTER×HUNTER"],"year":1998},"same":true},
{"a":{"titles":["Yotsuba&!"],"authors":["Kiyohiko Azuma"]},"b":{"titles":["Yotsuba&!"],"authors":["Azuma Kiyohiko"]},"same":true},
{"a":{"titles":["Kimetsu no Yaiba"],"authors":["Koyoharu Gotouge"],"year":2016},"b":{"titles":["Kimetsu no Yaiba"],"authors":["Gotōge Koyoharu"],"year":2016},"same":true},
{"a":{"titles":["Made in Abyss"]},"b":{"titles":["Made In Abyss"]},"same":true},
{"a":{"titles":["Tensei shitara Slime Datta Ken"]},"b":{"titles":["Tensei Shitara Slime Datta Ken"]},"same":true},
{"a":{"titles":["Tongari Boushi no Atelier","Witch Hat Atelier"]},"b":{"titles":["Witch Hat Atelier"]},"same":true},
{"a":{"titles":["Blue Lock"],"authors":["Muneyuki Kaneshiro"]},"b":{"titles":["BLUE LOCK"],"authors":["Kaneshiro Muneyuki"]},"same":true},
{"a":{"titles":["Mob Psycho 100"]},"b":{"titles":["Mob Psycho 100"],"authors":["ONE"]},"same":true},
{"a":{"titles":["Tate no Yuusha no Nariagari","The Rising of the Shield Hero"]},"b":{"titles":["The Rising of the Shield Hero"]},"same":true},
{"a":{"titles":["Kaiju No. 8"],"authors":["Naoya Matsumoto"]},"b":{"titles":["Kaiju No.8"],"authors":["Matsumoto Naoya"]},"same":true},
{"a":{"titles":["Dandadan"],"authors":["Yukinobu Tatsu"]},"b":{"titles":["DAN DA DAN"],"authors":["Tatsu Yukinobu"]},"same":true},
{"a":{"titles":["Jujutsu Kaisen"],"authors":["Gege Akutami"],"year":2018},"b":{"titles":["Jujutsu Kaisen"],"authors":["芥見下々"],"year":2018},"same":true},
{"a":{"titles":["Kingdom"],"authors":["Yasuhisa Hara"],"year":2006},"b":{"titles":["Kingdom"],"authors":["Hara Yasuhisa"],"year":2006},"same":true},
{"a":{"titles":["Vagabond"],"cover":"9f2c51a0d1e44b7a"},"b":{"titles":["Vagabond"],"cover":"9f2c51a0d1e44b7a"},"same":true},
{"a":{"titles":["Monster"],"phash":"f0f0e1c38f0f1e3c"},"b":{"titles":["MONSTER"],"phash":"f0f0e1c38f0f1e3d"},"same":true},
{"a":{"titles":["The Quintessential Quintuplets","Go-Toubun no Hanayome"]},"b":{"titles":["5-toubun no Hanayome","The Quintessential Quintuplets"]},"same":true},
{"a":{"titles":["Tokyo Ghoul"]},"b":{"titles":["Tokyo Ghoul:re"]},"same":false},
{"a":{"titles":["Is It Wrong to Try to Pick Up Girls in a Dungeon?"]},"b":{"titles":["Is It Wrong to Try to Pick Up Girls in a Dungeon? II"]},"same":false},
{"a":{"titles":["Ascendance of a Bookworm Part 2"]},"b":{"titles":["Ascendance of a Bookworm Part 3"]},"same":false},
{"a":{"titles":["Mushoku Tensei: Jobless Reincarnation"]},"b":{"titles":["Mushoku Tensei: Jobless Reincarnation Part 2"]},"same":false},
{"a":{"titles":["That Time I Got Reincarnated as a Slime"]},"b":{"titles":["That Time I Got Reincarnated as a Slime 2"]},"same":false},
{"a":{"titles":["Solo Leveling"]},"b":{"titles":["Solo Leveling: Ragnarok"]},"same":false},
{"a":{"titles":["Solo Leveling"],"authors":["Chugong"]},"b":{"titles":["Solo Leveling (Webtoon)"],"authors":["Chugong"]},"same":false},
{"a":{"titles":["One Piece"],"authors":["Eiichiro Oda"]},"b":{"titles":["One Piece (Colored)"],"authors":["Oda Eiichirou"]},"same":false},
{"a":{"titles":["Berserk"],"authors":["Kentaro Miura"]},"b":{"titles":["Berserk (Full Color)"],"authors":["Kentarou Miura"]},"same":false},
{"a":{"titles":["Overlord"],"authors":["Kugane Maruyama"]},"b":{"titles":["Overlord (Novel)"],"authors":["Kugane Maruyama"]},"same":false},
{"a":{"titles":["Monster"],"authors":["Naoki Urasawa"],"year":1994},"b":{"titles":["Monster"],"authors":["Kim Carnby"],"year":2016},"same":false},
{"a":{"titles":["Orange"]},"b":{"titles":["Orange"]},"same":false},
{"a":{"titles":["Hellsing"],"year":1997},"b":{"titles":["Hellsing"],"year":2010},"same":false},
{"a":{"titles":["Naruto"]},"b":{"titles":["Boruto: Naruto Next Generations"]},"same":false},
{"a":{"titles":["Dragon Ball"]},"b":{"titles":["Dragon Ball Super"]},"same":false},
{"a":{"titles":["Attack on Titan"]},"b":{"titles":["Attack on Titan: Before the Fall"]},"same":false},
{"a":{"titles":["Re:Zero Starting Life in Another World"]},"b":{"titles":["Re:Zero Starting Life in Another World Ex"]},"same":false},
{"a":{"titles":["Sword Art Online"]},"b":{"titles":["Sword Art Online Progressive"]},"same":false},
{"a":{"titles":["Sword Art Online Alternative: Gun Gale Online"]},"b":{"titles":["Sword Art Online"]},"same":false},
{"a":{"titles":["Vagabond"],"phash":"f0f0e1c38f0f1e3c"},"b":{"titles":["Vagabond"],"phash":"0f0f1e3c70f0e1c3"},"same":false},
{"a":{"titles":["Solo Leveling"],"authors":["Chugong"],"animated":true},"b":{"titles":["Solo Leveling"],"authors":["Chugong"],"animated":false},"same":false},
{"a":{"titles":["Tower of God"],"authors":["SIU"],"media":"webtoon"},"b":{"titles":["Tower of God"],"authors":["SIU"],"media":"manga"},"same":false},
{"a":{"titles":["Chainsaw Man"],"authors":["Tatsuki Fujimoto"],"color":"colored"},"b":{"titles":["Chainsaw Man"],"authors":["Tatsuki Fujimoto"],"color":"monochrome"},"same":false},
{"a":{"titles":["Chainsaw Man"],"authors":["Tatsuki Fujimoto"]},"b":{"titles":["Chainsaw Man"],"authors":["Tatsuki Fujimoto"],"color":"colored"},"same":false},
{"a":{"titles":["Fullmetal Alchemist"]},"b":{"titles":["Fullmetal Alchemist: Brotherhood"]},"same":false},
{"a":{"titles":["Spy x Family"]},"b":{"titles":["Spy x Family: Family Portrait"]},"same":false},
{"a":{"titles":["The Promised Neverland"],"authors":["Kaiu Shirai"]},"b":{"titles":["The Promised Neverland"],"authors":["Posuka Demizu","Kaiu Shirai"]},"same":true},
{"a":{"titles":["Grand Blue","Grand Blue Dreaming"],"authors":["Kenji Inoue"]},"b":{"titles":["Grand Blue Dreaming"],"authors":["Inoue Kenji"]},"same":true}
]}
""";

    public static void Register(TestSuite suite)
    {
        suite.Add("matching scorer: labelled public title pairs have no wrong merge and no missed match", () =>
        {
            using var temp = new Sandbox();
            var path = Path.Combine(temp.Path, "pairs.json");
            File.WriteAllText(path, KnownPairs);
            var report = PairScoringTool.Score(path);
            Check.True(report.Pairs >= 40, "at least 40 labelled pairs");
            Check.True(report.WrongMerges == 0 && report.MissedMatches == 0, string.Join(Environment.NewLine, report.Lines));
            using var output = new StringWriter();
            Check.Equal(0, PairScoringTool.Run(path, output));
            Check.True(output.ToString().Contains("0 wrong merges, 0 missed matches.", StringComparison.Ordinal));
        });

        suite.Add("matching scorer: the pair tool fails on a wrong merge and on an unreadable file", () =>
        {
            using var temp = new Sandbox();
            var path = Path.Combine(temp.Path, "pairs.json");
            File.WriteAllText(path, """{"pairs":[{"a":{"titles":["Made in Abyss"]},"b":{"titles":["Made In Abyss"]},"same":false}]}""");
            using var output = new StringWriter();
            Check.Equal(1, PairScoringTool.Run(path, output));
            Check.True(output.ToString().Contains("WRONG MERGE #1", StringComparison.Ordinal));
            File.WriteAllText(path, """{"pairs":[{"a":{"titles":["Made in Abyss"]},"same":true}]}""");
            Check.Equal(2, PairScoringTool.Run(path, new StringWriter()));
        });

        suite.Add("matching scorer: extra and missing words both lower the score, symmetrically", () =>
        {
            const string original = "The Rising of the Shield Hero";
            const string sequel = "The Rising of the Shield Hero 2";
            // The released ratio alone would pass the 0.94 floor; the missing-word penalty must not.
            Check.True(TextMatcher.AutomaticTitleSimilarity(original, sequel) >= MatchScorer.MinimumFloor);
            Check.True(MatchScorer.TitleScore(original, sequel) < MatchScorer.MinimumFloor);
            Check.True(Math.Abs(MatchScorer.TitleScore(original, sequel) - MatchScorer.TitleScore(sequel, original)) < 1e-12);
            var a = TextMatcher.NormalizeTitle("Re:Zero Starting Life in Another World");
            var b = TextMatcher.NormalizeTitle("Re:Zero Starting Life in Another World Ex");
            Check.True(MatchScorer.MissingWordPenalty(a, b) > 0);
            Check.True(Math.Abs(MatchScorer.MissingWordPenalty(a, b) - MatchScorer.MissingWordPenalty(b, a)) < 1e-12);
            // Spacing, punctuation, case and Hepburn long vowels cost nothing.
            Check.True(MatchScorer.TitleScore("SPY×FAMILY", "Spy x Family") >= .99);
            Check.True(MatchScorer.TitleScore("Shingeki no Kyojin", "Shingeki no Kyoujin") >= MatchScorer.MinimumFloor);
            Check.Equal(0.0, MatchScorer.MissingWordPenalty("fullmetal alchemist", "full metal alchemist"));
        });

        suite.Add("matching scorer: identity evidence blocks a perfect title and agreement raises confidence", () =>
        {
            MatchCandidate Side(string author = "", int? year = null, string phash = "") => new()
            { Titles = ["Monster"], Authors = author.Length > 0 ? [author] : [], Year = year, CoverPerceptualHash = phash };
            var conflict = MatchScorer.Score(Side("Naoki Urasawa", 1994), Side("Kim Carnby", 2016));
            Check.Equal(1.0, conflict.TitleScore);
            Check.True(!conflict.AutomaticMatch);
            Check.True(conflict.Blockers.Any(item => item.Kind == "author") && conflict.Blockers.Any(item => item.Kind == "year"));
            var cover = MatchScorer.Score(Side(phash: "f0f0e1c38f0f1e3c"), Side(phash: "0f0f1e3c70f0e1c3"));
            Check.True(cover.Blockers.Any(item => item.Kind == "cover"));
            var generic = MatchScorer.Score(Side(), Side());
            Check.True(generic.FloorPassed && !generic.AutomaticMatch && generic.Blockers.Any(item => item.Kind == "identity-evidence"));
            var agreed = MatchScorer.Score(Side("Naoki Urasawa", 1994), Side("Urasawa Naoki", 1995));
            Check.True(agreed.AutomaticMatch);
            Check.True(agreed.Score >= agreed.TitleScore && agreed.Evidence.Any(item => item.Kind == "author") && agreed.Evidence.Any(item => item.Kind == "year"));
            Check.Equal("agree", MatchScorer.AuthorEvidence(["Koyoharu Gotouge"], ["Gotōge Koyoharu"]).State);
            Check.Equal("neutral", MatchScorer.AuthorEvidence(["Gege Akutami"], ["芥見下々"]).State);
            Check.Equal("conflict", MatchScorer.AuthorEvidence(["Naoki Urasawa"], ["Kim Carnby"]).State);
            var json = agreed.ToJson();
            Check.True(json["floor_passed"]!.GetValue<bool>() && json["evidence"] is JsonArray && json["blockers"] is JsonArray);
        });

        suite.Add("matching scorer: editions never merge, decided from hints as well as titles", () =>
        {
            MatchCandidate Side(string title, string color = "", string media = "", bool? animated = null) => new()
            { Titles = [title], Authors = ["Tatsuki Fujimoto"], ColorHint = color, MediaHint = media, Animated = animated };
            Check.True(MatchScorer.Score(Side("Chainsaw Man"), Side("Chainsaw Man (Colored)")).Blockers.Any(item => item.Kind == "edition-colour"));
            Check.True(MatchScorer.Score(Side("Chainsaw Man"), Side("Chainsaw Man", color: "colored")).Blockers.Any(item => item.Kind == "edition-colour"));
            Check.True(MatchScorer.Score(Side("Chainsaw Man", color: "monochrome"), Side("Chainsaw Man")).AutomaticMatch);
            Check.True(MatchScorer.Score(Side("Chainsaw Man", media: "webtoon"), Side("Chainsaw Man", media: "manga")).Blockers.Any(item => item.Kind == "edition-media"));
            Check.True(MatchScorer.Score(Side("Chainsaw Man"), Side("Chainsaw Man (Novel)")).Blockers.Any(item => item.Kind == "edition-media"));
            Check.True(MatchScorer.Score(Side("Chainsaw Man", animated: true), Side("Chainsaw Man")).Blockers.Any(item => item.Kind == "edition-animated"));
            Check.Equal("Chainsaw Man", MatchScorer.EditionBaseTitle("Chainsaw Man (Colored)"));
        });

        suite.Add("matching profiles: the floor never goes below 0.94 and ready-made presets match 0.4.5", () =>
        {
            Check.Equal(MatchScorer.MinimumFloor, new MatchScoringOptions { Floor = .5 }.Normalized().Floor);
            Check.True(new MatchScoringOptions { Floor = .5, GenericFloor = .1 }.Normalized().GenericFloor >= MatchScorer.GenericTitleFloor);
            Check.Equal(1, new MatchScoringOptions { YearTolerance = 9 }.Normalized().YearTolerance);
            var ready = MatchProfiles.ReadyMadeProfiles();
            Check.Equal("title,creators,summary,gallery,strict", string.Join(',', ready.Select(profile => profile.Id)));
            Check.Equal(.97, ready[0].Options.Floor);
            Check.Equal(.90, ready[1].Options.RequiredAuthor ?? 0);
            var (simpleRule, simpleSettings) = MatchProfiles.Build(MatchProfiles.Simple, new JsonObject { ["strict_titles"] = true, ["require_creators"] = true, ["exact_year"] = true });
            var simple = MatchProfiles.OptionsFor(simpleRule, simpleSettings);
            Check.True(simple.Floor == .97 && simple.RequiredAuthor == .90 && simple.YearTolerance == 0);
            Check.Throws<MatchingProfileException>(() => MatchProfiles.Build(MatchProfiles.Advanced, new JsonObject { ["checks"] = new JsonObject { ["title"] = .9 } }));
            Check.Throws<MatchingProfileException>(() => MatchProfiles.Build(MatchProfiles.Simple, new JsonObject { ["unknown"] = true }));
            var (advancedRule, advancedSettings) = MatchProfiles.Build(MatchProfiles.Advanced, new JsonObject
            {
                ["checks"] = new JsonObject { ["title"] = .96 }, ["author_conflict"] = .1, ["require_identity_evidence"] = true,
                ["weights"] = new JsonObject { ["cover"] = .03 },
            });
            var advanced = MatchProfiles.OptionsFor(advancedRule, advancedSettings);
            Check.True(advanced.Floor == .96 && advanced.AuthorConflict == .35 && advanced.AlwaysRequireIdentityEvidence && advanced.CoverWeight == .03);
            var strict = MatchScorer.Score(new MatchCandidate { Titles = ["Made in Abyss"] }, new MatchCandidate { Titles = ["Made In Abyss"] }, advanced);
            Check.True(strict.FloorPassed && !strict.AutomaticMatch);
        });

        suite.Add("matching profiles: default source ranking puts official publishers first, then health, then quality", () =>
        {
            var ranked = SourceRanking.Order(
            [
                new SourceRank("a", "Scans A", false, "working", 1),
                new SourceRank("b", "MANGA Plus", true, "failed", 50),
                new SourceRank("c", "Scans C", false, "failed", 0),
                new SourceRank("d", "Scans D", false, "working", 5),
                new SourceRank("e", "K Manga", true, "working", 900),
            ]);
            Check.Equal("e,b,a,d,c", string.Join(',', ranked.Select(rank => rank.SourceId)));
        });
    }
}
