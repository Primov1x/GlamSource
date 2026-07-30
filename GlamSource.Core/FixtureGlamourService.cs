using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using GlamSource.Core;

namespace GlamSource.Core;

public sealed class FixtureGlamourService : IGlamourService
{
    private readonly string _fixturePath;
    private readonly IItemSourceService? _sourceService;
    private IReadOnlyList<EquipmentSlot>? _cached;

    public FixtureGlamourService(string fixturePath, IItemSourceService? sourceService = null)
    {
        _fixturePath = fixturePath;
        _sourceService = sourceService;
    }

    public IReadOnlyList<(uint id, string name)> SearchItems(string query) => Array.Empty<(uint, string)>();

    public IReadOnlyList<EquipmentSlot> GetTargetEquipment()
    {
        if (_cached != null)
            return _cached;

        var json = File.ReadAllText(_fixturePath);
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };

        var slots = JsonSerializer.Deserialize<List<FixtureSlot>>(json, options)
            ?? throw new JsonException("Fixture file is null or empty.");

        _cached = slots
            .Where(s => s.Slot != null)
            .Select(s =>
            {
                var actualSources = _sourceService?.GetSources(s.ActualItemId);
                var glamourSources = s.GlamourItemId.HasValue
                    ? _sourceService?.GetSources(s.GlamourItemId.Value)
                    : null;

                return new EquipmentSlot(
                    Slot: (EquipmentSlotType)s.Slot!,
                    ActualItemId: s.ActualItemId,
                    ActualItemName: s.ActualItemName ?? string.Empty,
                    GlamourItemId: s.GlamourItemId,
                    GlamourItemName: s.GlamourItemName,
                    ActualItemSources: actualSources,
                    GlamourItemSources: glamourSources);
            })
            .ToList()
            .AsReadOnly();

        return _cached;
    }

    private sealed record FixtureSlot(
        [property: JsonPropertyName("slot")] EquipmentSlotType? Slot,
        [property: JsonPropertyName("actualItemId")] uint ActualItemId,
        [property: JsonPropertyName("actualItemName")] string? ActualItemName,
        [property: JsonPropertyName("glamourItemId")] uint? GlamourItemId,
        [property: JsonPropertyName("glamourItemName")] string? GlamourItemName);
}
