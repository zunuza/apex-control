using System.Text;

namespace ApexControl.Core;

// Executes one plan step against the keyboard: send, then wait for the ack GG
// gets (all acks arrive on mi_01's input report).
public static class Runner
{
    public static void Execute(KeyboardLink link, PlanItem item, Action<PlanItem, byte[]>? onFetch = null, HashSet<string>? firmware = null, IOperationSink? sink = null)
    {
        if (item.DelayBeforeMs > 0) Thread.Sleep(item.DelayBeforeMs);

        if (item.Frame.IsFeature)
        {
            link.SetFeature(item.Frame.Data);
            Thread.Sleep(10);
            if (item.FetchAfter) onFetch?.Invoke(item, link.GetFeature());
            return;
        }

        link.DrainReplies();
        link.SendOutput(item.Frame.Data);
        switch (item.Expect)
        {
            case Reply.None:
                Thread.Sleep(15);
                break;
            case Reply.Version:
                // A version query is read-only, so it is safe to repeat once if the reply never shows up.
                byte[] v;
                try { v = link.WaitFor(r => r.Length >= 6 && r[0] == 0x34 && r[1] == 0x2E, 2000); }
                catch (TimeoutException)
                {
                    sink?.Info("  (no reply to a version query; retrying it once)");
                    Thread.Sleep(300);
                    link.DrainReplies();
                    link.SendOutput(item.Frame.Data);
                    v = link.WaitFor(r => r.Length >= 6 && r[0] == 0x34 && r[1] == 0x2E, 2000);
                }
                firmware?.Add(Encoding.ASCII.GetString(v, 0, 6));
                break;
            case Reply.F4Ack:
                link.WaitFor(r => r.Length >= 2 && r[0] == 0x00 && r[1] == 0x01, 2000);
                break;
            case Reply.ReadAck:
                link.WaitFor(r => r.Length >= 2 && r[0] == 0x85 && r[1] == 0x01, 2000);
                break;
            case Reply.BeginAck:
                link.WaitFor(r => r.Length >= 2 && r[0] == 0x88 && r[1] == 0x01, 3000);
                break;
            case Reply.CommitAck:
                link.WaitFor(r => r.Length >= 2 && r[0] == 0x05 && r[1] == 0x01, 3000);
                break;
            case Reply.EbAck:
                link.WaitFor(r => r.Length >= 2 && r[0] == 0x01 && r[1] == 0x00, 3000);
                break;
        }
    }
}
