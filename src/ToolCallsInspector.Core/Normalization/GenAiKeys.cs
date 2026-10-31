namespace ToolCallsInspector.Core.Normalization;

/// <summary>Attribute keys from the OTel GenAI conventions and the Copilot namespaces.</summary>
public static class GenAiKeys
{
    public const string OperationName = "gen_ai.operation.name";
    public const string AgentName = "gen_ai.agent.name";
    public const string ConversationId = "gen_ai.conversation.id";
    public const string RequestModel = "gen_ai.request.model";
    public const string ResponseModel = "gen_ai.response.model";
    public const string ResponseId = "gen_ai.response.id";
    public const string RequestMaxTokens = "gen_ai.request.max_tokens";
    public const string FinishReasons = "gen_ai.response.finish_reasons";
    public const string InputTokens = "gen_ai.usage.input_tokens";
    public const string OutputTokens = "gen_ai.usage.output_tokens";
    public const string CacheReadTokens = "gen_ai.usage.cache_read.input_tokens";
    public const string CacheCreationTokens = "gen_ai.usage.cache_creation.input_tokens";
    public const string ReasoningTokens = "gen_ai.usage.reasoning.output_tokens";
    public const string ReasoningTokensLegacy = "gen_ai.usage.reasoning_tokens";
    public const string ToolName = "gen_ai.tool.name";
    public const string ToolType = "gen_ai.tool.type";
    public const string ToolCallId = "gen_ai.tool.call.id";
    public const string ToolCallArguments = "gen_ai.tool.call.arguments";
    public const string ToolCallResult = "gen_ai.tool.call.result";
    public const string InputMessages = "gen_ai.input.messages";
    public const string OutputMessages = "gen_ai.output.messages";
    public const string SystemInstructions = "gen_ai.system_instructions";
    public const string ToolDefinitions = "gen_ai.tool.definitions";
    public const string ErrorType = "error.type";
    public const string ProcessExitCode = "process.exit.code";
    public const string SessionId = "session.id";

    public const string CopilotSessionId = "github.copilot.session.id";
    public const string CopilotHookDecision = "github.copilot.hook.decision";
    public const string CopilotTimeToFirstToken = "github.copilot.time_to_first_token";

    public const string ChatSessionId = "copilot_chat.chat_session_id";
    public const string ParentChatSessionId = "copilot_chat.parent_chat_session_id";
    public const string ChatLegacySessionId = "copilot_chat.session_id";
    public const string DebugLogLabel = "copilot_chat.debug_log_label";
    public const string MaxPromptTokens = "copilot_chat.request.max_prompt_tokens";
    public const string TimeToFirstToken = "copilot_chat.time_to_first_token";
    public const string UserRequest = "copilot_chat.user_request";
    public const string ReasoningContent = "copilot_chat.reasoning_content";
    public const string HookType = "copilot_chat.hook_type";

    public static class Operations
    {
        public const string Chat = "chat";
        public const string InvokeAgent = "invoke_agent";
        public const string ExecuteTool = "execute_tool";
        public const string ExecuteHook = "execute_hook";
    }
}
