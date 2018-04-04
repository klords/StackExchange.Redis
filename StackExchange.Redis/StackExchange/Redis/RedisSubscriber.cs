﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace StackExchange.Redis
{
    public partial class ConnectionMultiplexer
    {
        private readonly Dictionary<RedisChannel, Subscription> subscriptions = new Dictionary<RedisChannel, Subscription>();

        internal static bool TryCompleteHandler<T>(EventHandler<T> handler, object sender, T args, bool isAsync) where T : EventArgs
        {
            if (handler == null) return true;
            if (isAsync)
            {
                foreach (EventHandler<T> sub in handler.GetInvocationList())
                {
                    try
                    { sub.Invoke(sender, args); }
                    catch
                    { }
                }
                return true;
            }
            return false;
        }

        internal Task AddSubscription(RedisChannel channel, Action<RedisChannel, RedisValue> handler, CommandFlags flags, object asyncState)
        {
            if (handler != null)
            {
                lock (subscriptions)
                {
                    if (subscriptions.TryGetValue(channel, out Subscription sub))
                    {
                        sub.Add(handler);
                    }
                    else
                    {
                        sub = new Subscription(handler);
                        subscriptions.Add(channel, sub);
                        var task = sub.SubscribeToServer(this, channel, flags, asyncState, false);
                        if (task != null) return task;
                    }
                }
            }
            return CompletedTask<bool>.Default(asyncState);
        }

        internal ServerEndPoint GetSubscribedServer(RedisChannel channel)
        {
            if (!channel.IsNullOrEmpty)
            {
                lock (subscriptions)
                {
                    if (subscriptions.TryGetValue(channel, out Subscription sub))
                    {
                        return sub.GetOwner();
                    }
                }
            }
            return null;
        }

        internal void OnMessage(RedisChannel subscription, RedisChannel channel, RedisValue payload)
        {
            ICompletable completable = null;
            lock (subscriptions)
            {
                if (subscriptions.TryGetValue(subscription, out Subscription sub))
                {
                    completable = sub.ForInvoke(channel, payload);
                }
            }
            if (completable != null) UnprocessableCompletionManager.CompleteSyncOrAsync(completable);
        }

        internal Task RemoveAllSubscriptions(CommandFlags flags, object asyncState)
        {
            Task last = CompletedTask<bool>.Default(asyncState);
            lock (subscriptions)
            {
                foreach (var pair in subscriptions)
                {
                    pair.Value.Remove(null); // always wipes
                    var task = pair.Value.UnsubscribeFromServer(pair.Key, flags, asyncState, false);
                    if (task != null) last = task;
                }
                subscriptions.Clear();
            }
            return last;
        }

        internal Task RemoveSubscription(RedisChannel channel, Action<RedisChannel, RedisValue> handler, CommandFlags flags, object asyncState)
        {
            lock (subscriptions)
            {
                if (subscriptions.TryGetValue(channel, out Subscription sub) && sub.Remove(handler))
                {
                    subscriptions.Remove(channel);
                    var task = sub.UnsubscribeFromServer(channel, flags, asyncState, false);
                    if (task != null) return task;
                }
            }
            return CompletedTask<bool>.Default(asyncState);
        }

        internal void ResendSubscriptions(ServerEndPoint server)
        {
            if (server == null) return;
            lock (subscriptions)
            {
                foreach (var pair in subscriptions)
                {
                    pair.Value.Resubscribe(pair.Key, server);
                }
            }
        }

        internal bool SubscriberConnected(RedisChannel channel = default(RedisChannel))
        {
            var server = GetSubscribedServer(channel);
            if (server != null) return server.IsConnected;

            server = SelectServer(-1, RedisCommand.SUBSCRIBE, CommandFlags.DemandMaster, default(RedisKey));
            return server?.IsConnected == true;
        }

        internal long ValidateSubscriptions()
        {
            lock (subscriptions)
            {
                long count = 0;
                foreach (var pair in subscriptions)
                {
                    if (pair.Value.Validate(this, pair.Key)) count++;
                }
                return count;
            }
        }

        private sealed class Subscription
        {
            private Action<RedisChannel, RedisValue> handler;
            private List<ServerEndPoint> owners = new List<ServerEndPoint>();

            public Subscription(Action<RedisChannel, RedisValue> value) => handler = value;

            public void Add(Action<RedisChannel, RedisValue> value) => handler += value;

            public ICompletable ForInvoke(RedisChannel channel, RedisValue message)
            {
                var tmp = handler;
                return tmp == null ? null : new MessageCompletable(channel, message, tmp);
            }

            public bool Remove(Action<RedisChannel, RedisValue> value)
            {
                if (value == null)
                { // treat as blanket wipe
                    handler = null;
                    return true;
                }
                else
                {
                    return (handler -= value) == null;
                }
            }

            public Task SubscribeToServer(ConnectionMultiplexer multiplexer, RedisChannel channel, CommandFlags flags, object asyncState, bool internalCall)
            {
                // subscribe to all masters in cluster for keyspace/keyevent notifications
                if (channel.IsKeyspaceChannel) {
                    return SubscribeToMasters(multiplexer, channel, flags, asyncState, internalCall);
                }
                return SubscribeToSingleServer(multiplexer, channel, flags, asyncState, internalCall);
            }

            private Task SubscribeToSingleServer(ConnectionMultiplexer multiplexer, RedisChannel channel, CommandFlags flags, object asyncState, bool internalCall)
            {
                var cmd = channel.IsPatternBased ? RedisCommand.PSUBSCRIBE : RedisCommand.SUBSCRIBE;
                var selected = multiplexer.SelectServer(-1, cmd, flags, default(RedisKey));

                lock (owners)
                {
                    if (selected == null || owners.Contains(selected)) return null;
                    owners.Add(selected);
                }

                var msg = Message.Create(-1, flags, cmd, channel);
                if (internalCall) msg.SetInternalCall();
                return selected.QueueDirectAsync(msg, ResultProcessor.TrackSubscriptions, asyncState);
            }

            private Task SubscribeToMasters(ConnectionMultiplexer multiplexer, RedisChannel channel, CommandFlags flags, object asyncState, bool internalCall)
            {
                List<Task> subscribeTasks = new List<Task>();
                var cmd = channel.IsPatternBased ? RedisCommand.PSUBSCRIBE : RedisCommand.SUBSCRIBE;
                var masters = multiplexer.GetServerSnapshot().Where(s => !s.IsSlave && s.EndPoint.Equals(s.ClusterConfiguration.Origin));

                lock (owners)
                {
                    foreach (var master in masters)
                    {
                        if (owners.Contains(master)) continue;
                        owners.Add(master);
                        var msg = Message.Create(-1, flags, cmd, channel);
                        if (internalCall) msg.SetInternalCall();
                        subscribeTasks.Add(master.QueueDirectAsync(msg, ResultProcessor.TrackSubscriptions, asyncState));
                    }
                }

                return Task.WhenAll(subscribeTasks);
            }

            public Task UnsubscribeFromServer(RedisChannel channel, CommandFlags flags, object asyncState, bool internalCall)
            {
                if (owners.Count == 0) return null;

                List<Task> queuedTasks = new List<Task>();
                var cmd = channel.IsPatternBased ? RedisCommand.PUNSUBSCRIBE : RedisCommand.UNSUBSCRIBE;
                var msg = Message.Create(-1, flags, cmd, channel);
                if (internalCall) msg.SetInternalCall();
                foreach (var owner in owners)
                    queuedTasks.Add(owner.QueueDirectAsync(msg, ResultProcessor.TrackSubscriptions, asyncState));
                owners.Clear();
                return Task.WhenAll(queuedTasks.ToArray());
            }

            internal ServerEndPoint GetOwner()
            {
                var owner = owners?[0]; // we subscribe to arbitrary server, so why not return one
                return Interlocked.CompareExchange(ref owner, null, null);
            }

            internal void Resubscribe(RedisChannel channel, ServerEndPoint server)
            {
                bool hasOwner; 

                lock (owners)
                {
                    hasOwner = owners.Contains(server);
                }

                if (server != null && hasOwner)
                {
                    var cmd = channel.IsPatternBased ? RedisCommand.PSUBSCRIBE : RedisCommand.SUBSCRIBE;
                    var msg = Message.Create(-1, CommandFlags.FireAndForget, cmd, channel);
                    msg.SetInternalCall();
                    server.QueueDirectFireAndForget(msg, ResultProcessor.TrackSubscriptions);
                }
            }

            internal bool Validate(ConnectionMultiplexer multiplexer, RedisChannel channel)
            {
                bool changed = false;
                if (owners.Count != 0 && !owners.All(o => o.IsSelectable(RedisCommand.PSUBSCRIBE)))
                {
                    if (UnsubscribeFromServer(channel, CommandFlags.FireAndForget, null, true) != null)
                    {
                        changed = true;
                    }
                    owners.Clear();
                }
                if (owners.Count == 0 && SubscribeToServer(multiplexer, channel, CommandFlags.FireAndForget, null, true) != null)
                {
                    changed = true;
                }
                return changed;
            }
        }
    }

    internal sealed class RedisSubscriber : RedisBase, ISubscriber
    {
        internal RedisSubscriber(ConnectionMultiplexer multiplexer, object asyncState) : base(multiplexer, asyncState)
        {
        }

        public EndPoint IdentifyEndpoint(RedisChannel channel, CommandFlags flags = CommandFlags.None)
        {
            var msg = Message.Create(-1, flags, RedisCommand.PUBSUB, RedisLiterals.NUMSUB, channel);
            msg.SetInternalCall();
            return ExecuteSync(msg, ResultProcessor.ConnectionIdentity);
        }

        public Task<EndPoint> IdentifyEndpointAsync(RedisChannel channel, CommandFlags flags = CommandFlags.None)
        {
            var msg = Message.Create(-1, flags, RedisCommand.PUBSUB, RedisLiterals.NUMSUB, channel);
            msg.SetInternalCall();
            return ExecuteAsync(msg, ResultProcessor.ConnectionIdentity);
        }

        public bool IsConnected(RedisChannel channel = default(RedisChannel))
        {
            return multiplexer.SubscriberConnected(channel);
        }

        public override TimeSpan Ping(CommandFlags flags = CommandFlags.None)
        {
            // can't use regular PING, but we can unsubscribe from something random that we weren't even subscribed to...
            RedisValue channel = Guid.NewGuid().ToByteArray();
            var msg = ResultProcessor.TimingProcessor.CreateMessage(-1, flags, RedisCommand.UNSUBSCRIBE, channel);
            return ExecuteSync(msg, ResultProcessor.ResponseTimer);
        }

        public override Task<TimeSpan> PingAsync(CommandFlags flags = CommandFlags.None)
        {
            // can't use regular PING, but we can unsubscribe from something random that we weren't even subscribed to...
            RedisValue channel = Guid.NewGuid().ToByteArray();
            var msg = ResultProcessor.TimingProcessor.CreateMessage(-1, flags, RedisCommand.UNSUBSCRIBE, channel);
            return ExecuteAsync(msg, ResultProcessor.ResponseTimer);
        }

        public long Publish(RedisChannel channel, RedisValue message, CommandFlags flags = CommandFlags.None)
        {
            if (channel.IsNullOrEmpty) throw new ArgumentNullException(nameof(channel));
            var msg = Message.Create(-1, flags, RedisCommand.PUBLISH, channel, message);
            return ExecuteSync(msg, ResultProcessor.Int64);
        }

        public Task<long> PublishAsync(RedisChannel channel, RedisValue message, CommandFlags flags = CommandFlags.None)
        {
            if (channel.IsNullOrEmpty) throw new ArgumentNullException(nameof(channel));
            var msg = Message.Create(-1, flags, RedisCommand.PUBLISH, channel, message);
            return ExecuteAsync(msg, ResultProcessor.Int64);
        }

        public void Subscribe(RedisChannel channel, Action<RedisChannel, RedisValue> handler, CommandFlags flags = CommandFlags.None)
        {
            var task = SubscribeAsync(channel, handler, flags);
            if ((flags & CommandFlags.FireAndForget) == 0) Wait(task);
        }

        public Task SubscribeAsync(RedisChannel channel, Action<RedisChannel, RedisValue> handler, CommandFlags flags = CommandFlags.None)
        {
            if (channel.IsNullOrEmpty) throw new ArgumentNullException(nameof(channel));
            return multiplexer.AddSubscription(channel, handler, flags, asyncState);
        }

        public EndPoint SubscribedEndpoint(RedisChannel channel)
        {
            var server = multiplexer.GetSubscribedServer(channel);
            return server?.EndPoint;
        }

        public void Unsubscribe(RedisChannel channel, Action<RedisChannel, RedisValue> handler = null, CommandFlags flags = CommandFlags.None)
        {
            var task = UnsubscribeAsync(channel, handler, flags);
            if ((flags & CommandFlags.FireAndForget) == 0) Wait(task);
        }

        public void UnsubscribeAll(CommandFlags flags = CommandFlags.None)
        {
            var task = UnsubscribeAllAsync(flags);
            if ((flags & CommandFlags.FireAndForget) == 0) Wait(task);
        }

        public Task UnsubscribeAllAsync(CommandFlags flags = CommandFlags.None)
        {
            return multiplexer.RemoveAllSubscriptions(flags, asyncState);
        }

        public Task UnsubscribeAsync(RedisChannel channel, Action<RedisChannel, RedisValue> handler = null, CommandFlags flags = CommandFlags.None)
        {
            if (channel.IsNullOrEmpty) throw new ArgumentNullException(nameof(channel));
            return multiplexer.RemoveSubscription(channel, handler, flags, asyncState);
        }
    }
}
