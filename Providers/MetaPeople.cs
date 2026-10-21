using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;

namespace Gelato.Providers;

/// <summary>
/// The people of a title as its meta names them. The insert saves them before it answers and the
/// metadata refresh saves them again: both read them here, so the refresh writes the same people
/// in the same order.
/// </summary>
/// <remarks>
/// Each gets its place in the meta as its sort order. Jellyfin lists the cast by sort order, and the
/// refresh takes another provider's (TMDB's) for every person that has none: the cast shown first
/// would be reordered by it.
/// </remarks>
public static class MetaPeople
{
    public static List<PersonInfo> From(StremioMeta meta)
    {
        var people = new List<PersonInfo>();

        var cast = meta.App_Extras?.Cast;
        if (cast is { Count: > 0 })
        {
            foreach (var member in cast)
            {
                if (string.IsNullOrWhiteSpace(member.Name))
                    continue;
                people.Add(
                    new PersonInfo
                    {
                        Name = member.Name,
                        Role = member.Character,
                        Type = PersonKind.Actor,
                        ImageUrl = member.Photo,
                    }
                );
            }
        }
        else
        {
            // Cinemeta sends the cast as names only.
            foreach (var name in SplitNames(meta.Cast))
                people.Add(new PersonInfo { Name = name, Type = PersonKind.Actor });
        }

        AddCrew(people, meta.App_Extras?.Directors, meta.Director, PersonKind.Director);
        AddCrew(people, meta.App_Extras?.Writers, meta.Writer, PersonKind.Writer);

        for (var i = 0; i < people.Count; i++)
            people[i].SortOrder = i;
        return people;
    }

    private static void AddCrew(
        List<PersonInfo> people,
        List<StremioCast>? members,
        string? names,
        PersonKind kind
    )
    {
        if (members is { Count: > 0 })
        {
            foreach (var m in members)
            {
                if (!string.IsNullOrWhiteSpace(m.Name))
                    people.Add(
                        new PersonInfo
                        {
                            Name = m.Name,
                            Type = kind,
                            ImageUrl = m.Photo,
                        }
                    );
            }
        }
        else
        {
            foreach (var name in SplitNames(names))
                people.Add(new PersonInfo { Name = name, Type = kind });
        }
    }

    private static string[] SplitNames(string? names) =>
        string.IsNullOrWhiteSpace(names)
            ? []
            : names.Split(
                ',',
                StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries
            );
}
