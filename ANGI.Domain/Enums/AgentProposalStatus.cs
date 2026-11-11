namespace ANGI.Domain.Enums;

/// <summary>Validation and confirmation lifecycle of an immutable proposal.</summary>
public enum AgentProposalStatus { Ready, Invalid, Superseded, Committed, Discarded, Expired }
