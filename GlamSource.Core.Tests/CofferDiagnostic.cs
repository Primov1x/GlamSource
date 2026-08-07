using System;
using System.Linq;
using GlamSource.Core;
using Lumina.Excel.Sheets;

namespace GlamSource.Core.Tests;

public class CofferDiagnostic
{
    [Fact]
    public void DumpCofferData()
    {
        var gameData = new Lumina.GameData(@"D:\FF\game\sqpack", null);
        var service = new LuminaItemSourceService(gameData);
        var sources = service.GetSources(44352);
        Console.WriteLine($"Sources for 44352: {sources.Count}");
        foreach (var s in sources)
            Console.WriteLine($"  {s.Type}: {s.Description}");

        // Check Recipe for 44352
        var recipes = gameData.GetExcelSheet<Recipe>()?.ToArray() ?? Array.Empty<Recipe>();
        var recipeMatches = recipes.Where(r => r.ItemResult.RowId == 44352).ToList();
        Console.WriteLine($"\nRecipe rows with ItemResult=44352: {recipeMatches.Count}");
        foreach (var r in recipeMatches)
            Console.WriteLine($"  RecipeId={r.RowId} Result={r.ItemResult.RowId}");

        // Check all recipe result IDs near 44352
        var recipeResultIds = recipes.Select(r => r.ItemResult.RowId).Distinct().ToList();
        var nearbyRecipeItems = recipeResultIds.Where(id => id >= 44340 && id <= 44360).OrderBy(x => x).ToList();
        Console.WriteLine($"\nRecipe result items near 44352: [{string.Join(", ", nearbyRecipeItems)}]");
        Console.WriteLine($"44352 in recipe results: {recipeResultIds.Contains(44352)}");

        // Check Quest sheet for 44352
        var quests = gameData.GetExcelSheet<Quest>()?.ToArray() ?? Array.Empty<Quest>();
        var questMatches = quests.Where(q => q.Reward.Any(r => r.RowId == 44352)).ToList();
        Console.WriteLine($"\nQuest rows with Reward=44352: {questMatches.Count}");
        foreach (var q in questMatches)
            Console.WriteLine($"  QuestId={q.RowId} RewardCount={q.Reward.Count}");

        // Check all quest reward IDs near 44352
        var questRewardIds = quests.SelectMany(q => q.Reward).Select(r => r.RowId).Distinct().ToList();
        var nearbyQuestItems = questRewardIds.Where(id => id >= 44340 && id <= 44360).OrderBy(x => x).ToList();
        Console.WriteLine($"\nQuest reward items near 44352: [{string.Join(", ", nearbyQuestItems)}]");
        Console.WriteLine($"44352 in quest rewards: {questRewardIds.Contains(44352)}");
    }
}
