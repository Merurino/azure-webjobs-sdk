﻿using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Azure.Jobs;

namespace Dashboard.Data
{
    // Implements the causality logger interfaces
    internal class CausalityLogger : ICausalityLogger, ICausalityReader
    {
        private readonly IAzureTable<TriggerReasonEntity> _table;
        private readonly IFunctionInstanceLookup _logger; // needed to lookup parents

        public CausalityLogger(IAzureTable<TriggerReasonEntity> table, IFunctionInstanceLookup logger)
        {
            _table = table;
            _logger = logger;
        }

        // ICausalityLogger
        public void LogTriggerReason(TriggerReason reason)
        {
            if (reason == null)
            {
                throw new ArgumentNullException("reason");
            }

            if (reason.ParentGuid == Guid.Empty)
            {
                // Nothing to log. 
                return;
            }

            if (reason.ChildGuid == Guid.Empty)
            {
                throw new InvalidOperationException("Child guid must be set.");
            }

            string rowKey = TableClient.GetTickRowKey();
            string partitionKey = reason.ParentGuid.ToString();
            var value = new TriggerReasonEntity(reason);
            _table.Write(partitionKey, rowKey, values: value);
            _table.Flush();
        }

        // ICausalityReader
        public IEnumerable<TriggerReason> GetChildren(Guid parent)
        {
            return GetChildren(parent, null, null, 1000).Select(v => v.Data.Payload).ToArray();
        }

        public IEnumerable<TriggerReasonEntity> GetChildren(Guid parent, string rowKeyExclusiveUpperBound, string rowKeyExclusiveLowerBound, int limit)
        {
            var values = _table.Enumerate(parent.ToString());
            if (rowKeyExclusiveLowerBound != null)
            {
                values = values.Where(v => v.RowKey.CompareTo(rowKeyExclusiveLowerBound) > 0);
            }
            if (rowKeyExclusiveUpperBound != null)
            {
                values = values.Where(v => v.RowKey.CompareTo(rowKeyExclusiveUpperBound) < 0);
            }
            values = values.Take(limit);

            return values.ToArray();
        }

        public Guid GetParent(Guid child)
        {
            if (_logger == null)
            {
                throw new InvalidOperationException("In Write-only mode.");
            }
            var entry = _logger.Lookup(child);
            TriggerReason reason = entry.FunctionInstance.TriggerReason;

            return reason.ParentGuid;
        }
    }
}