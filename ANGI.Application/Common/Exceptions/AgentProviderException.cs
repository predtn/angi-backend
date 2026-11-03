using ANGI.Application.Common.Models.Agent;

namespace ANGI.Application.Common.Exceptions;

/// <summary>Reports classified AI availability failures through the existing AGENT_UNAVAILABLE contract.</summary>
public sealed class AgentProviderException(AgentLlmFailure failure) : ServiceUnavailableException("AGENT_UNAVAILABLE", MessageFor(failure))
{
    public AgentLlmFailure Failure { get; } = failure;

    /// <summary>Provides user-facing guidance without returning internal provider details or credentials.</summary>
    private static string MessageFor(AgentLlmFailure failure) => failure.Kind switch
    {
        _ when failure.Reason == "budget_control" => "AI đang tạm dừng theo hạn mức sử dụng. Vui lòng liên hệ quản trị viên.",
        AgentLlmFailureKind.Timeout => "Dịch vụ AI phản hồi quá lâu. Vui lòng thử lại sau.",
        AgentLlmFailureKind.TurnTimeout => "Agent đã hết thời gian xử lý yêu cầu. Vui lòng thử lại sau.",
        AgentLlmFailureKind.RateLimited when failure.Reason == "daily_quota_exceeded" => "Dịch vụ AI đã vượt hạn mức sử dụng trong ngày. Vui lòng thử lại sau khi hạn mức được đặt lại.",
        AgentLlmFailureKind.RateLimited when failure.Reason == "rate_limit" => "Dịch vụ AI đang giới hạn số yêu cầu hoặc token mỗi phút. Vui lòng thử lại sau thời gian chờ.",
        AgentLlmFailureKind.RateLimited => "Dịch vụ AI đang giới hạn yêu cầu hoặc đã hết hạn mức sử dụng. Vui lòng thử lại sau.",
        AgentLlmFailureKind.Configuration or AgentLlmFailureKind.Authentication => "Dịch vụ AI chưa được cấu hình hợp lệ. Vui lòng liên hệ quản trị viên.",
        AgentLlmFailureKind.Network => "Không kết nối được tới dịch vụ AI. Vui lòng thử lại sau.",
        AgentLlmFailureKind.RejectedRequest => "Không thể gửi yêu cầu hợp lệ tới dịch vụ AI. Vui lòng thử lại sau.",
        AgentLlmFailureKind.ProviderUnavailable => "Dịch vụ AI tạm thời không khả dụng. Vui lòng thử lại sau.",
        AgentLlmFailureKind.Truncated => "AI chưa hoàn tất phản hồi trong giới hạn xử lý. Vui lòng thử lại sau.",
        AgentLlmFailureKind.Refused => "Dịch vụ AI không thể trả lời yêu cầu này. Vui lòng điều chỉnh nội dung.",
        _ => "Dịch vụ AI trả phản hồi chưa hợp lệ. Vui lòng thử lại sau."
    };
}
