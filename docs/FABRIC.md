# Fabric Eventstream publishing (optional)

When enabled, the sales API sends an `engagement.submitted` JSON event to a Microsoft Fabric Eventstream after each submission. It is off by default and a Fabric failure never blocks or fails a submission (it is logged as a warning, with a 10 second timeout).

## Setup
1. In Fabric, create an Eventstream and add a **Custom endpoint** source.
2. Copy its Event Hubs compatible **connection string** and **entity name** (the connection string may already include `EntityPath=`).
3. Set them outside the repo, from `apps\sales\server\ArchiTech.Api`:

```
dotnet user-secrets set "Fabric:Enabled" "true"
dotnet user-secrets set "Fabric:ConnectionString" "<connection string>"
dotnet user-secrets set "Fabric:EventHubName" "<entity name>"
```
Or use env vars `Fabric__Enabled`, `Fabric__ConnectionString`, `Fabric__EventHubName`.
4. Route the Eventstream to a Lakehouse, Warehouse or KQL database destination.

## Payload
eventType, eventId, occurredUtc, engagementId, customerId, customerName, salesperson, ownerEmail, meetingDate, users, endpoints, hour estimates, taskCount, Planner and deployment sync status, createdUtc.

Note: the payload includes customer name and salesperson email. Restrict workspace access accordingly.

Events are fire-and-forget (no retry queue). Use Fabric mirroring of the SQL database if you need guaranteed completeness.
