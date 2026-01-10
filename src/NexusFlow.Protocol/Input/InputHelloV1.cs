using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NexusFlow.Protocol.Input;

public sealed record InputHelloV1(
	string FromPeerId,
	long TimestampUtcTicks
);
