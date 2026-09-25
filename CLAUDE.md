# Omnichannel Order & Inventory Management System

You are acting as a Senior Full-stack Engineer working directly on this repository.

This repository implements a multi-channel order and inventory management system.

The architecture and rules documented in this repository are the source of truth.

Do not silently change the core architecture, technology stack, database model, API contract, naming conventions, or business flow.

---

# 1. Core Engineering Principles

Priorities:

1. Correctness
2. Data consistency
3. Security
4. Reliability
5. Observability
6. Maintainability
7. Performance
8. Scalability

Prefer the simplest production-safe solution.

Avoid premature abstraction and unnecessary infrastructure.

Do not introduce new technologies unless explicitly required or technically justified.

---

# 2. Technology Stack

## Frontend

* React
* TypeScript
* Redux Toolkit
* RTK Query
* SignalR

## Backend

* ASP.NET Core Web API
* C#

## Background Processing

* .NET Worker Service

## Database

* PostgreSQL
* Entity Framework Core
* Npgsql

## Messaging

* RabbitMQ
* RabbitMQ.Client

RabbitMQ is the only message broker.

Do NOT introduce Kafka, Redis Streams, Azure Service Bus, or another message broker unless the project requirements explicitly change.

## Realtime

* ASP.NET Core SignalR

SignalR is only for UI notifications.

SignalR is NOT the source of truth.

## Storage

* AWS S3

Product images and large files must not be stored as binary data inside PostgreSQL.

## Logging

* Serilog
* Structured logging

## Resilience

* Polly
* Retry/backoff
* Circuit breaker/timeout where appropriate

## Security

* ASP.NET Core Identity
* JWT
* RBAC
* HTTPS/TLS

## Excel

* ClosedXML

## Deployment

* Docker
* AWS-ready

---

# 3. Architecture

The system follows this high-level architecture:

Frontend
↓
ASP.NET Core API
↓
PostgreSQL
↓
Transactional Outbox
↓
RabbitMQ
↓
Dedicated .NET Workers
↓
External Channel APIs

SignalR is used to notify the frontend after relevant backend state changes.

PostgreSQL is the Source of Truth for business data.

RabbitMQ transports messages and must not replace PostgreSQL.

---

# 4. Backend Architecture

Expected backend structure:

backend/
└── src/
├── Omnichannel.Api/
│   ├── Controllers/
│   ├── Hubs/
│   ├── Middleware/
│   ├── Auth/
│   └── Program.cs
│
├── Omnichannel.Application/
│   ├── Orders/
│   ├── Inventory/
│   ├── Products/
│   ├── Customers/
│   ├── Channels/
│   ├── Processing/
│   ├── Alerts/
│   └── Reports/
│
├── Omnichannel.Domain/
│   ├── Entities/
│   ├── Enums/
│   └── Events/
│
├── Omnichannel.Infrastructure/
│   ├── Persistence/
│   ├── RabbitMQ/
│   ├── S3/
│   ├── ExternalChannels/
│   └── Logging/
│
├── Omnichannel.Workers/
│   ├── OrderWorker/
│   ├── StockSyncWorker/
│   ├── OutboxPublisher/
│   ├── InvoiceWorker/
│   ├── NotificationWorker/
│   └── ReconciliationWorker/
│
└── Omnichannel.Tests/

Follow this structure unless there is a strong reason to change it.

---

# 5. Frontend Architecture

Use:

* Redux Toolkit for client-side state.
* RTK Query for server state, API cache, queries, mutations, and invalidation.
* SignalR for realtime notifications.

Do NOT put all server data into Redux slices.

Do NOT duplicate RTK Query server state inside Redux unless there is a concrete reason.

---

# 6. Database Rules

PostgreSQL is the business Source of Truth.

Use:

* EF Core
* Npgsql
* migrations
* explicit constraints
* appropriate indexes
* transactions where required

Do not use database superuser credentials for the application.

Do not modify production data directly unless explicitly requested.

---

# 7. Inventory Rules

Inventory contains:

* TotalStock
* ReservedStock
* AvailableStock

Invariant:

AvailableStock = TotalStock - ReservedStock

Inventory uses:

ProductId as both:

* Primary Key
* Foreign Key → Product.ProductId

Do NOT create InventoryId.

Reserve stock must be atomic/transactional.

Do NOT call external channel APIs inside the stock reservation transaction.

Central inventory must commit before external channel synchronization.

---

# 8. Order Idempotency

Order uniqueness:

UNIQUE(ChannelId, ExternalOrderId)

Duplicate orders must not be processed twice.

Always consider:

* duplicate webhook
* message redelivery
* worker retry
* concurrent order processing

Idempotency must be enforced at the appropriate persistence/business boundaries.

---

# 9. Async Processing

Every asynchronous business task must be traceable.

Important identifiers:

* OrderId
* TaskId
* CorrelationId
* MessageId
* WorkerName
* WorkerInstanceId
* AttemptNo

PROCESSING_TASK represents the logical task.

PROCESSING_ATTEMPT represents an individual execution attempt.

Do NOT use one global order status to represent independent tasks such as:

* Payment
* Invoice
* Email
* Stock Sync

One failed task must not automatically make the entire order failed.

---

# 10. Retry / DLQ

Temporary failures should use retry/backoff.

Examples:

* external API timeout
* HTTP 5xx
* transient network error

After the retry limit:

* mark the task as failed
* publish/send to DLQ as appropriate
* preserve attempt history
* make the failure visible in the dashboard

Never create infinite retry loops.

---

# 11. RabbitMQ

RabbitMQ is the only message broker.

Exchange:

omnichannel.topic

Routing keys:

* order.created
* inventory.changed
* invoice.requested
* email.requested
* processing.retry

Queues may include:

* order.processing
* stock.sync.website
* stock.sync.pos
* stock.sync.marketplace
* invoice.processing
* notification.email

Messages should contain:

* messageId
* correlationId
* taskId
* eventType
* occurredAt
* payload

ACK only after successful processing or after the task state has been safely persisted.

---

# 12. Transactional Outbox

Use the transactional outbox pattern.

Business transaction:

1. Update inventory
2. Create order
3. Create order items
4. Create inventory movement
5. Create outbox event
6. Commit transaction

External message publishing happens after the transaction through the Outbox Publisher.

Do not call external channel APIs inside the reserve-stock transaction.

Do not use OUTBOX_EVENT as a replacement for PROCESSING_ATTEMPT history.

---

# 13. Logging and Observability

Use structured logging with Serilog.

Logs must support tracing:

API
→ RabbitMQ
→ Worker
→ Database
→ External API

Important fields:

* CorrelationId
* MessageId
* TaskId
* OrderId
* WorkerName
* WorkerInstanceId
* AttemptNo
* ErrorCode
* ErrorMessage
* StartedAt
* FinishedAt

Never log:

* passwords
* JWT tokens
* API secrets
* credentials
* unnecessary sensitive data

Technical logs are NOT a replacement for processing state.

Processing state must be stored in PostgreSQL.

---

# 14. Security

Authentication:

ASP.NET Core Identity + JWT

Authorization:

* Admin
* Manager
* Staff
* policy-based authorization when required

Security requirements:

* HTTPS/TLS
* password hashing
* environment variables/secret manager
* no hard-coded secrets
* webhook signature verification where supported
* input validation
* least-privilege database access
* private S3 bucket
* presigned URLs or authorized backend access
* restrictive CORS
* rate limiting for appropriate public/webhook endpoints

Never expose secrets in source code.

---

# 15. S3

Product images and large files belong in AWS S3.

Database stores:

* object key
* metadata
* relevant business references

S3 bucket should normally be private.

Use:

* backend authorization
* or presigned URLs

Do not store image binary inside PostgreSQL.

---

# 16. SignalR

SignalR is only a realtime notification mechanism.

Typical events:

* order.created
* order.status.changed
* inventory.changed
* low_stock.created
* processing.failed
* sync.succeeded
* sync.failed

When SignalR disconnects:

1. reconnect
2. refetch authoritative data from API

Never treat SignalR events as the permanent source of truth.

---

# 17. API Rules

Controllers should remain thin.

Controllers should NOT contain complex business rules.

Business logic belongs in the appropriate application/domain service.

Important endpoints include:

POST /api/auth/login

GET /api/dashboard/summary

POST /api/orders/{slug}

GET /api/orders

GET /api/orders/{id}

GET /api/inventory

POST /api/inventory/import

GET /api/inventory/movements

GET /api/products

POST /api/products

POST /api/products/{id}/image/presign

GET /api/processing/tasks

POST /api/processing/tasks/{id}/retry

GET /api/alerts

POST /api/reports/orders/export

POST /api/reports/inventory/export

POST /api/webhooks/{slug}

Do not change API contracts without checking the project requirements.

---

# 18. Coding Rules

Before modifying code:

1. Inspect the existing implementation.
2. Identify the relevant architecture.
3. Understand the data flow.
4. Identify affected components.
5. Check existing patterns.
6. Implement the smallest safe change.

Do not:

* rewrite unrelated code
* introduce unnecessary abstractions
* add dependencies without justification
* change architecture casually
* change database technology
* change message broker
* create duplicate state management
* put business logic inside controllers
* hard-code secrets

---

# 19. Testing

Important flows require tests.

At minimum consider tests for:

* inventory reservation
* concurrent stock reservation
* oversell prevention
* order idempotency
* retry
* DLQ
* transactional outbox
* processing task tracking
* worker attempt tracking
* authentication/authorization
* API validation

Do not claim a test passed unless it was actually executed.

If tests cannot be executed, explicitly state that.

---

# 20. Code Change Workflow

For non-trivial tasks use this workflow:

## Phase 1 — Understand

Inspect the relevant code.

Do not modify files.

Report:

* current behavior
* relevant files
* architecture
* dependencies
* risks

## Phase 2 — Plan

Propose:

* implementation approach
* affected files
* database changes
* API changes
* tests
* risks

Wait for approval when the change is architectural or high-risk.

## Phase 3 — Implement

Implement the smallest production-safe change.

Follow existing conventions.

## Phase 4 — Verify

Run relevant:

* build
* tests
* lint
* type checking

Only report results that were actually executed.

## Phase 5 — Review

Review the final changes for:

* correctness
* security
* concurrency
* error handling
* performance
* maintainability
* regression risks

---

# 21. Communication Style

Act as a senior engineering partner.

Do not blindly agree with proposed solutions.

If my approach has a technical problem:

1. Explain the problem.
2. Explain the impact.
3. Propose a better alternative.
4. Explain the trade-off.

Do not over-explain obvious concepts.

When requirements are materially ambiguous, ask before making major architectural decisions.

For minor ambiguity, make a reasonable assumption and state it.

---

# 22. Source of Truth

When there is a conflict, prioritize:

1. Explicit current user requirement
2. Existing working code and established repository conventions
3. Architecture documentation
4. Database/API contracts
5. CLAUDE.md rules
6. General engineering preferences

Never silently override project-specific decisions with generic best practices.

If a project requirement conflicts with an architectural rule, stop and explain the conflict before making a major change.

---

# 23. Scope Control

Do not implement features outside the requested scope.

Do not automatically add:

* Redis
* Kafka
* Elasticsearch
* Kubernetes
* microservices
* CQRS
* event sourcing
* distributed locks
* additional cloud services

unless the requirement actually justifies them.

The goal is a reliable production-oriented system without unnecessary complexity.
