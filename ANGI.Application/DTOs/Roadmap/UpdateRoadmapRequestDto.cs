using System.Text.Json.Serialization;

namespace ANGI.Application.DTOs.Roadmap;

/// <summary>Contains the optional trip overview fields accepted by RM-04.</summary>
public sealed class UpdateRoadmapRequestDto
{
    private string? _title;
    private DateOnly? _startDate;
    private short? _numDays;
    private decimal? _budgetAmount;
    private string? _originMode;
    private decimal? _originLatitude;
    private decimal? _originLongitude;
    private string? _originLabel;

    public string? Title { get => _title; set { _title = value; TitleSpecified = true; } }
    public DateOnly? StartDate { get => _startDate; set { _startDate = value; StartDateSpecified = true; } }
    public short? NumDays { get => _numDays; set { _numDays = value; NumDaysSpecified = true; } }
    public decimal? BudgetAmount { get => _budgetAmount; set { _budgetAmount = value; BudgetAmountSpecified = true; } }
    public string? OriginMode { get => _originMode; set { _originMode = value; OriginModeSpecified = true; } }
    public decimal? OriginLatitude { get => _originLatitude; set { _originLatitude = value; OriginLatitudeSpecified = true; } }
    public decimal? OriginLongitude { get => _originLongitude; set { _originLongitude = value; OriginLongitudeSpecified = true; } }
    public string? OriginLabel { get => _originLabel; set { _originLabel = value; OriginLabelSpecified = true; } }

    [JsonIgnore] public bool TitleSpecified { get; private set; }
    [JsonIgnore] public bool StartDateSpecified { get; private set; }
    [JsonIgnore] public bool NumDaysSpecified { get; private set; }
    [JsonIgnore] public bool BudgetAmountSpecified { get; private set; }
    [JsonIgnore] public bool OriginModeSpecified { get; private set; }
    [JsonIgnore] public bool OriginLatitudeSpecified { get; private set; }
    [JsonIgnore] public bool OriginLongitudeSpecified { get; private set; }
    [JsonIgnore] public bool OriginLabelSpecified { get; private set; }
}
