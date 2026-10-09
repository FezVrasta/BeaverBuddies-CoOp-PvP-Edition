using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TimberNet
{
    public class MultiSocketListener : ISocketListener
    {
        private readonly List<ISocketListener> listeners = new List<ISocketListener>();

        private readonly ConcurrentQueueWithWait<ISocketStream> accepted = new ConcurrentQueueWithWait<ISocketStream>();
        private bool isAccepting = false;
        private bool isStopped = false;

        public IEnumerable<ISocketListener> Listeners => listeners;

        public MultiSocketListener(params ISocketListener[] listeners) 
        {
            this.listeners.AddRange(listeners);
        }

        public ISocketStream AcceptClient()
        {
            if (!isAccepting)
            {
                StartAccpting();
                isAccepting = true;
            }
            accepted.WaitAndTryDequeue(out ISocketStream socket);
            return socket;
        }

        private void StartAccpting()
        {
            foreach (var listener in listeners)
            {
                Task.Run(() =>
                {
                    while (!isStopped)
                    {
                        ISocketStream socket;
                        try
                        {
                            socket = listener.AcceptClient();
                        }
                        catch (Exception)
                        {
                            // A stopped listener ends its wait with an error
                            if (isStopped) return;
                            // One failed accept (a connection reset mid-handshake,
                            // say) mustn't end this listener's loop for good: nobody
                            // observes this task, so throwing would silently stop
                            // every later player coming in this way
                            Thread.Sleep(100);
                            continue;
                        }
                        if (isStopped) return;
                        if (socket != null) accepted.Enqueue(socket);
                    }
                });
            }
        }

        public void Start()
        {
            listeners.ForEach(listener => listener.Start());
        }

        public void Stop()
        {
            isStopped = true;
            listeners.ForEach(listener =>
            {
                try { listener.Stop(); }
                catch (Exception) { }
            });
            // Wakes up whoever is waiting for the next client, with none
            accepted.Enqueue(null);
        }

        public T GetListener<T>()
        {
            return (T)listeners.Find(listener => listener is T);
        }
    }
}
