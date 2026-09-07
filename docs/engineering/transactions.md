# Transactions

Every write operation is represented by a transaction.

```text
CREATED
  -> VALIDATING
  -> AWAITING_APPROVAL
  -> APPROVED
  -> EXECUTING
  -> VALIDATING_RESULT
  -> COMMITTED
```

Failure states:

```text
REJECTED
FAILED
ROLLED_BACK
EXPIRED
```

Transactions must support idempotency and stale-context protection.
