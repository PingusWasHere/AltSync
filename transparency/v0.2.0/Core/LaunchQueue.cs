namespace AltSync.Core;

public sealed record LaunchRequest(long AccountId, string Name, string PlaceId, string JobId);
public sealed record QueueEntry(LaunchRequest Request, string Status, string Error = "");
public sealed record QueueStatus(bool Active, bool Paused, bool Cancelling, string Message, IReadOnlyList<QueueEntry> Entries);
public sealed class LaunchQueue
{
    private readonly object sync = new();
    private QueueEntry[] entries = [];
    private CancellationTokenSource? cancellation;
    private bool active, paused;
    private string message = "";
    public QueueStatus Status { get { lock(sync) return new(active,paused,cancellation?.IsCancellationRequested??false,message,entries.ToArray()); } }
    public void Pause(bool value) { lock(sync) { if(active) paused=value; } }
    public void Cancel() { lock(sync) { if(active) cancellation?.Cancel(); } }
    public LaunchRequest[] Failed() { lock(sync) return entries.Where(e=>e.Status=="failed").Select(e=>e.Request).ToArray(); }
    public async Task Run(IReadOnlyList<LaunchRequest> requests, Func<LaunchRequest,Task> launch, int delaySeconds, bool adaptive, CancellationToken lifetime=default, Func<ResourceLoad>? resources=null)
    {
        CancellationToken token;
        lock(sync)
        {
            if(active) throw new InvalidOperationException("A launch queue is already running.");
            if(requests.Count is <1 or >100 || delaySeconds is <3 or >30)throw new ArgumentException("Choose between 1 and 100 accounts.");
            entries=requests.Select(r=>new QueueEntry(r,"waiting")).ToArray();active=true;paused=false;message="Starting accounts";
            cancellation=CancellationTokenSource.CreateLinkedTokenSource(lifetime);token=cancellation.Token;
        }
        resources??=SystemResources.Sample;
        try
        {
            for(int i=0;i<requests.Count;i++)
            {
                token.ThrowIfCancellationRequested();
                await WaitForResume(token);
                if(adaptive)
                {
                    for(int n=0;n<10;n++)
                    {
                        var load=resources();if(load.AvailableMb>=1024&&load.CpuPercent<85)break;
                        lock(sync)message="Spacing launches while the PC is busy";
                        await Task.Delay(2000,token);await WaitForResume(token);
                    }
                }
                token.ThrowIfCancellationRequested();
                lock(sync){entries[i]=entries[i] with{Status="starting"};message=$"Starting {i+1} of {requests.Count}";}
                // Cancellation stops remaining launches; an account already starting is allowed to finish.
                try { await launch(requests[i]);lock(sync)entries[i]=entries[i] with{Status="started"}; }
                catch(Exception ex) { lock(sync)entries[i]=entries[i] with{Status="failed",Error=ex is System.Net.Http.HttpRequestException?"Roblox could not be reached.":ex.Message.Length>250?ex.Message[..250]:ex.Message}; }
                if(i+1<requests.Count){lock(sync)message="Waiting before the next account";await Task.Delay(TimeSpan.FromSeconds(delaySeconds),token);}
            }
            lock(sync)message="Launch queue finished";
        }
        catch(OperationCanceledException)when(token.IsCancellationRequested)
        {
            lock(sync){entries=entries.Select(e=>e.Status=="waiting"?e with{Status="cancelled"}:e).ToArray();message="Remaining launches cancelled";}
        }
        finally { lock(sync){active=false;paused=false;cancellation?.Dispose();cancellation=null;} }
    }
    private async Task WaitForResume(CancellationToken token)
    { while(true){lock(sync){if(!paused)return;message="Launch queue paused";}await Task.Delay(200,token);} }
}
