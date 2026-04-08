#nullable disable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

using MTShared.Network;
using MTShared.Types;

namespace MTTextClient.Core
{
    public class AlertStore
    {
        // Active alerts: key = alertId
        private readonly ConcurrentDictionary<Int64, AlertInfoData> _alerts = new ConcurrentDictionary<Int64, AlertInfoData>();

        // Alert history entries
        private readonly ConcurrentQueue<AlertHistoryEntry> _history = new ConcurrentQueue<AlertHistoryEntry>();
        private readonly int _maxHistory;

        public AlertStore(int maxHistory = 200)
        {
            _maxHistory = maxHistory;
        }

        #region Active Alerts

        public void SetAlerts(Dictionary<Int64, AlertInfoData> alerts)
        {
            _alerts.Clear();
            foreach (KeyValuePair<Int64, AlertInfoData> kvp in alerts)
            {
                _alerts[kvp.Key] = kvp.Value;
            }
        }

        public void AddOrUpdate(AlertInfoData alert)
        {
            _alerts[alert.id] = alert;
        }

        public void Remove(Int64 alertId)
        {
            _alerts.TryRemove(alertId, out _);
        }

        public bool TryGet(Int64 alertId, out AlertInfoData alert)
        {
            return _alerts.TryGetValue(alertId, out alert);
        }

        public IReadOnlyList<AlertInfoData> GetAll()
        {
            List<AlertInfoData> list = new List<AlertInfoData>();
            foreach (KeyValuePair<Int64, AlertInfoData> kvp in _alerts)
            {
                list.Add(kvp.Value);
            }
            return list;
        }

        public int Count
        {
            get { return _alerts.Count; }
        }

        #endregion

        #region History

        public void AddHistory(AlertHistoryEntry entry)
        {
            _history.Enqueue(entry);
            while (_history.Count > _maxHistory)
            {
                _history.TryDequeue(out _);
            }
        }

        public List<AlertHistoryEntry> GetHistory(int count = 50)
        {
            List<AlertHistoryEntry> all = new List<AlertHistoryEntry>(_history);
            if (all.Count <= count)
            {
                return all;
            }
            return all.GetRange(all.Count - count, count);
        }

        public void ClearHistory()
        {
            while (_history.TryDequeue(out _)) { }
        }

        #endregion

        public void Clear()
        {
            _alerts.Clear();
            ClearHistory();
        }
    }

    public class AlertHistoryEntry
    {
        public DateTime ReceivedAtUtc { get; set; }
        public ExchangeType ExchangeType { get; set; }
        public string ActionType { get; set; }
        public string RawJson { get; set; }

        public AlertHistoryEntry(ExchangeType exchangeType, string actionType, string rawJson)
        {
            ReceivedAtUtc = DateTime.UtcNow;
            ExchangeType = exchangeType;
            ActionType = actionType;
            RawJson = rawJson;
        }
    }
}
