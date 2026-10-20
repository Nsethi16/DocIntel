using DocIntel.Core.Channels;
using DocIntel.Core.Features.AskAi;
using Microsoft.Teams.Apps;

namespace DocIntel.Api.Channels;

/// <summary>Sends one answer to the original verified personal conversation using the native SDK.</summary>
public sealed class TeamsChannelTransport(TeamsBotApplication teams, TeamsChannelOptions options) : IChannelTransport
{
    public string Name => "Teams";

    public async Task SendAsync(AskAiRun run, ChannelDelivery target, CancellationToken cancellationToken = default)
    {
        options.Validate();
        if (!options.Enabled || target.Profile != options.Profile || target.Transport != Name ||
            run.TenantId != options.ApplicationTenantId || String.IsNullOrWhiteSpace(target.DestinationId) ||
            target.DestinationId.Length > 512 || target.DestinationId.Any(Char.IsControl))
            throw new InvalidOperationException("The trusted Teams routing profile changed or is invalid.");
        // Keep one bounded send: do not split a large answer into partially delivered, replay-prone messages.
        await teams.SendAsync(target.DestinationId,
            new MessageActivityInput().WithText(TeamsAnswerFormatter.Format(run, options)),
            serviceUrl: new Uri(options.ServiceUrl), cancellationToken: cancellationToken);
    }
}
