// CSP – Cost Management Export (billing account scope)
// Auth: SAS token (required at billing account scope — managed identity not supported here)
// Deploy with: az deployment tenant create --template-file export-billing.bicep ...
//
// Requires tenant-level deployment permissions (Global Admin or Billing Account Owner).

targetScope = 'tenant'

param runTimestamp string = utcNow('yyyy-MM-ddTHH:mm:ssZ')

// ── Billing Account ───────────────────────────────────────────────────────────

@description('Billing Account ID (the value after /providers/Microsoft.Billing/billingAccounts/).')
param billingAccountId string

// ── Export Destination ────────────────────────────────────────────────────────

@description('Name of the export task (e.g., daily-cost-export).')
param exportName string

@description('Storage account ARM resource ID.')
param storageAccountResourceId string

@description('Destination blob container name.')
param containerName string = 'cost-exports'

@description('Root folder path inside the container.')
param rootFolderPath string = 'exports'

@description('SAS token granting acwl access to the destination container. Do NOT include leading ?.')
@secure()
param sasToken string

// ── Export Definition ─────────────────────────────────────────────────────────

@allowed(['Csv', 'Parquet'])
param format string = 'Csv'

@allowed(['ActualCost', 'AmortizedCost', 'FocusCost', 'Usage'])
param definitionType string = 'ActualCost'

@allowed(['Daily', 'Monthly'])
param granularity string = 'Daily'

@description('Timeframe preset. Use Custom with timePeriodFrom/timePeriodTo for fixed ranges.')
@allowed(['MonthToDate', 'BillingMonthToDate', 'TheCurrentMonth', 'TheLastMonth', 'WeekToDate', 'Custom'])
param timeframe string = 'MonthToDate'

@description('Custom period start (yyyy-MM-dd). Only used when timeframe == Custom.')
param timePeriodFrom string = ''

@description('Custom period end (yyyy-MM-dd). Only used when timeframe == Custom.')
param timePeriodTo string = ''

// ── Schedule ──────────────────────────────────────────────────────────────────

@allowed(['Daily', 'Weekly', 'Monthly', 'Annually'])
param recurrence string = 'Daily'

@allowed(['Active', 'Inactive'])
param scheduleStatus string = 'Active'

@description('Recurrence start (UTC ISO yyyy-MM-ddTHH:mm:ssZ). Must be in the future.')
param recurrenceFrom string

@description('Recurrence end (UTC ISO). Defaults to far-future if left blank.')
param recurrenceTo string = '2099-12-31T00:00:00Z'

// ── Definition object (conditional timePeriod) ────────────────────────────────

var definitionBase = {
  type: definitionType
  timeframe: timeframe
  dataSet: {
    granularity: granularity
    configuration: {}
  }
}

var exportDefinition = timeframe == 'Custom'
  ? union(definitionBase, { timePeriod: { from: timePeriodFrom, to: timePeriodTo } })
  : definitionBase

// ── Billing Account + Export Resources ───────────────────────────────────────

resource billingAccount 'Microsoft.Billing/billingAccounts@2024-04-01' existing = {
  name: billingAccountId
}

resource export 'Microsoft.CostManagement/exports@2025-03-01' = {
  name: exportName
  scope: billingAccount
  properties: {
    format: format
    definition: exportDefinition
    deliveryInfo: {
      destination: {
        type: 'AzureBlob'
        resourceId: storageAccountResourceId
        container: containerName
        rootFolderPath: rootFolderPath
        sasToken: sasToken
      }
    }
    schedule: {
      recurrence: recurrence
      recurrencePeriod: {
        from: recurrenceFrom
        to: recurrenceTo
      }
      status: scheduleStatus
    }
    dataOverwriteBehavior: 'CreateNewReport'
    compressionMode: 'none'
  }
}

// ── Outputs ───────────────────────────────────────────────────────────────────

output exportResourceId string = export.id
