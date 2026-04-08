using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

using MTShared.Network;

namespace MTTextClient.Core
{
    public class NotificationEntry
    {
        public DateTime ReceivedAtUtc { get; set; }
        public string ProfileName { get; set; }
        public string NotificationType { get; set; }
        public string Message { get; set; }
        public string JsonData { get; set; }
        public Int64 CreationTime { get; set; }

        public NotificationEntry(string profileName, string notificationType, string message, string jsonData, Int64 creationTime)
        {
            ReceivedAtUtc = DateTime.UtcNow;
            ProfileName = profileName;
            NotificationType = notificationType;
            Message = message;
            JsonData = jsonData;
            CreationTime = creationTime;
        }
    }

    public class NotificationStore
    {
        private readonly ConcurrentQueue<NotificationEntry> _notifications = new ConcurrentQueue<NotificationEntry>();
        private readonly int _maxEntries;

        public NotificationStore(int maxEntries = 500)
        {
            _maxEntries = maxEntries;
        }

        public void Add(NotificationEntry entry)
        {
            _notifications.Enqueue(entry);
            while (_notifications.Count > _maxEntries)
            {
                _notifications.TryDequeue(out _);
            }
        }

        public List<NotificationEntry> GetAll()
        {
            return new List<NotificationEntry>(_notifications);
        }

        public List<NotificationEntry> GetRecent(int count)
        {
            List<NotificationEntry> all = new List<NotificationEntry>(_notifications);
            if (all.Count <= count)
            {
                return all;
            }
            return all.GetRange(all.Count - count, count);
        }

        public void Clear()
        {
            while (_notifications.TryDequeue(out _)) { }
        }

        public int Count
        {
            get { return _notifications.Count; }
        }
    }
}
