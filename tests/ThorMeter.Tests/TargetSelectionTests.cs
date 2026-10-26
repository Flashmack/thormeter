using ThorMeter.Core;

namespace ThorMeter.Tests;

public class TargetSelectionTests
{
    [Fact]
    public void MainCandidateExcludesKnownPlayersAndPetsAndDoesNotNameABoss()
    {
        TargetStatistics[] targets =
        [
            new(1, "Player", EntityKind.Player, 10_000),
            new(2, "Pet", EntityKind.Pet, 9_000),
            new(3, null, EntityKind.Unknown, 8_000),
            new(4, "NPC", EntityKind.Npc, 7_000)
        ];
        Assert.Equal(3ul, TargetSelection.MainCandidate(targets)?.EntityId);
        Assert.Null(TargetSelection.MainCandidate(targets.Take(2)));
    }

    [Fact]
    public void CategoryAndSearchCanFindAnUnknownByHexLabelOrDecimalId()
    {
        Assert.True(TargetSelection.Matches(EntityKind.Unknown, "Unidentified (#B75)", 2933,
            EntityKind.Unknown, "b75"));
        Assert.True(TargetSelection.Matches(EntityKind.Unknown, "Unidentified (#B75)", 2933,
            EntityKind.Unknown, "2933"));
        Assert.False(TargetSelection.Matches(EntityKind.Player, "Unidentified (#B75)", 2933,
            EntityKind.Npc, "b75"));
    }
}
