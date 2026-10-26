using ANGI.Application.Common.Interfaces.UseCases.Agent;
using ANGI.Application.Services.Agent;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.UseCases.Agent.CleanupConversations;
using ANGI.Application.UseCases.Agent.ConfirmProposal;
using ANGI.Application.UseCases.Agent.CreateConversation;
using ANGI.Application.UseCases.Agent.GetConversation;
using ANGI.Application.UseCases.Agent.SendMessage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ANGI.Application.UseCases.Agent;

/// <summary>Registers Agent business orchestration and its public use-case interfaces.</summary>
public static class AgentModule
{
    /// <summary>Registers scoped business components without referencing an LLM SDK or HTTP framework.</summary>
    public static IServiceCollection AddAgentUseCases(this IServiceCollection services)
    {
        services.TryAddSingleton<AgentLimits>();
        services.AddScoped<AgentStatePolicy>();
        services.AddScoped<AgentContextBuilder>();
        services.AddScoped<AgentProposalBuilder>();
        services.AddScoped<AgentProposalValidator>();
        services.AddScoped<AgentToolExecutor>();
        services.AddScoped<AgentRouteEstimator>();
        services.AddScoped<AgentOrchestrator>();
        services.AddScoped<ICreateAgentConversationUseCase, CreateAgentConversationUseCase>();
        services.AddScoped<IGetAgentConversationUseCase, GetAgentConversationUseCase>();
        services.AddScoped<ISendAgentMessageUseCase, SendAgentMessageUseCase>();
        services.AddScoped<IConfirmAgentProposalUseCase, ConfirmAgentProposalUseCase>();
        services.AddScoped<ICleanupAgentConversationsUseCase, CleanupAgentConversationsUseCase>();
        return services;
    }
}
