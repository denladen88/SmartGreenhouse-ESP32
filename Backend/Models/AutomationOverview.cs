namespace SmartGreenhouse.Backend.Models;

public record AutomationOverview(
    string Controller,
    string Evaluation,
    IReadOnlyList<AutomationRule> Rules);

public record AutomationRule(
    string Id,
    string Actuator,
    string Purpose,
    string Trigger,
    string Stop,
    string Safety);
