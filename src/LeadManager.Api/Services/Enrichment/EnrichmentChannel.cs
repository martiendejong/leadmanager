namespace LeadManager.Api.Services.Enrichment;

public class EnrichmentChannel
{
    private readonly System.Threading.Channels.Channel<Guid> _channel =
        System.Threading.Channels.Channel.CreateUnbounded<Guid>();
    public System.Threading.Channels.ChannelWriter<Guid> Writer => _channel.Writer;
    public System.Threading.Channels.ChannelReader<Guid> Reader => _channel.Reader;
}
