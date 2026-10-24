using System.Reflection;
using Gelato;
using MediaBrowser.Controller.Entities;

var policy = typeof(GelatoManager).GetMethod("ShouldAdoptWatchState", BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new Exception("production adoption policy missing");
var copy = typeof(GelatoManager).GetMethod("CopyAdoptedWatchState", BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new Exception("production adoption copy missing");
var checks = 0;
var older = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
var newer = older.AddDays(1);
UserItemData State(bool played = false, int count = 0, long position = 0, DateTime? date = null) =>
    new() { Key = "synthetic", Played = played, PlayCount = count, PlaybackPositionTicks = position, LastPlayedDate = date };
bool Adopt(UserItemData local, UserItemData source) =>
    (bool)(policy.Invoke(null, [local, source]) ?? throw new Exception("null decision"));
void Apply(UserItemData local, UserItemData source)
{
    if (Adopt(local, source))
        copy.Invoke(null, [local, source]);
}
void Check(string name, bool result)
{
    if (!result)
        throw new Exception($"FAIL: {name}");
    checks++;
}

var local = State();
Apply(local, State(played: true, count: 1, position: 600_000_000));
Check("null/null meaningful virtual adopts into empty local", local.Played && local.PlayCount == 1 && local.PlaybackPositionTicks == 600_000_000 && local.LastPlayedDate is null);
local = State();
Apply(local, State(played: true, count: 2, position: 600_000_000, date: newer));
Check("normal timestamped virtual adopts", local.Played && local.PlayCount == 2 && local.PlaybackPositionTicks == 600_000_000 && local.LastPlayedDate > newer);
local = State(played: true, count: 5, position: 800_000_000, date: newer);
Apply(local, State(played: true, count: 2, position: 100_000_000, date: older));
Check("newer local state preserved", local.PlayCount == 5 && local.PlaybackPositionTicks == 800_000_000 && local.LastPlayedDate == newer);
local = State(played: true, count: 4, position: 700_000_000, date: newer);
Apply(local, State(played: true, count: 8, position: 100_000_000, date: newer));
Check("equal timestamp conflict keeps local", local.PlayCount == 4 && local.PlaybackPositionTicks == 700_000_000);
local = State();
Apply(local, State(position: 200_000_000));
Check("undated resume-only state adopts", !local.Played && local.PlaybackPositionTicks == 200_000_000);
local = State();
Apply(local, State(played: true, count: 3));
Check("watched flag and play count adopt", local.Played && local.PlayCount == 3 && local.PlaybackPositionTicks == 0);
var userA = State();
var userB = State(played: true, count: 3, position: 300_000_000);
Apply(userA, State(position: 100_000_000));
Apply(userB, State(played: true, count: 1, position: 100_000_000));
Check("independent users keep separate decisions", userA.PlaybackPositionTicks == 100_000_000 && userB.PlaybackPositionTicks == 300_000_000 && userB.PlayCount == 3);
Apply(userA, State(position: 100_000_000));
Check("repeated undated adoption is idempotent", userA.PlaybackPositionTicks == 100_000_000);
local = State(played: true, count: 2, position: 500_000_000, date: older);
Apply(local, State(played: true, count: 3, date: newer));
Check("newer watched source intentionally clears older resume", local.Played && local.PlayCount == 3 && local.PlaybackPositionTicks == 0 && local.LastPlayedDate > newer);
local = State(position: 400_000_000, date: older);
Apply(local, State(count: 3, date: newer));
Check("count-only source does not clear local resume", local.PlayCount == 3 && local.PlaybackPositionTicks == 400_000_000);
local = State(position: 400_000_000);
Apply(local, State(played: true, count: 3, date: newer));
Check("dated source cannot override meaningful undated local", !local.Played && local.PlayCount == 0 && local.PlaybackPositionTicks == 400_000_000);
local = State(played: true, count: 2, date: newer);
Apply(local, State(position: 100_000_000));
Check("undated source cannot override meaningful dated local", local.Played && local.PlayCount == 2 && local.PlaybackPositionTicks == 0);
local = State();
Apply(local, State());
Check("empty source does not update local", !local.Played && local.PlayCount == 0 && local.PlaybackPositionTicks == 0);
Console.WriteLine($"Watch-state migration policy PASS cases={checks}");
