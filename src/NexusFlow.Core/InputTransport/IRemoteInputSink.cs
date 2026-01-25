using NexusFlow.Protocol.Input;

namespace NexusFlow.Core.InputTransport;

public interface IRemoteInputSink
{
	void Apply(InputEventV1 ev);
}
