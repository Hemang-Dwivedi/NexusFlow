using System.Threading;
using System.Threading.Tasks;
using NexusFlow.UI.ViewModels;

namespace NexusFlow.UI.Services;

public interface IPairingDialogService
{
	Task<bool> ShowCompareCodeAsync(PairingDialogViewModel vm, CancellationToken ct);
}
