"""IR, validation, policy, transaction and approval behaviour."""

from __future__ import annotations

import uuid
from datetime import timedelta

import pytest
from conftest import IDENTITY, NOW_UTC, make_operation

from engineerpc.approvals import ApprovalService, HumanApproval
from engineerpc.contracts import AuthenticatedIdentity, ProjectContext
from engineerpc.ir import (
    BlockParameter,
    BlockType,
    EngineeringOperationType,
    ProgrammingLanguage,
    validate_ir,
)
from engineerpc.policy import DefaultEngineeringPolicy, OperationRisk
from engineerpc.transactions import (
    EngineeringTransaction,
    InvalidTransactionTransitionError,
    TransactionState,
    TransactionStateMachine,
)
from engineerpc.validation import CreateBlockOperationValidator


def test_canonical_create_block_intent_has_no_ir_errors():
    operation = make_operation(inputs=(BlockParameter("Start", "Bool"),))

    assert validate_ir(operation) == []
    assert operation.ir_version == "1.0"
    assert operation.operation_type == EngineeringOperationType.CreateBlock


def test_missing_block_name_is_rejected():
    assert "Block name is required." in validate_ir(make_operation(name=" "))


def test_controller_name_is_optional_and_defaults_to_none():
    operation = make_operation(inputs=(BlockParameter("Start", "Bool"),))

    assert operation.controller_name is None
    assert validate_ir(operation) == []


def test_validator_rejects_invalid_identifier_and_duplicate_parameters():
    operation = make_operation(
        name="1Invalid",
        inputs=(BlockParameter("Start", "Bool"), BlockParameter("start", "Bool")),
    )

    errors = CreateBlockOperationValidator().validate(operation).errors

    assert any("Block name must start with a letter or underscore" in e for e in errors)
    assert "Input parameter name 'start' is duplicated." in errors


def test_validator_requires_parameter_data_type():
    operation = make_operation(inputs=(BlockParameter("Start", "  "),))

    errors = CreateBlockOperationValidator().validate(operation).errors

    assert "Input parameter 'Start' must declare a data type." in errors


def test_validator_detects_output_colliding_with_input():
    operation = make_operation(
        inputs=(BlockParameter("Signal", "Bool"),),
        outputs=(BlockParameter("Signal", "Bool"),),
    )

    errors = CreateBlockOperationValidator().validate(operation).errors

    assert "Block interface parameter name 'Signal' is duplicated." in errors


def test_default_policy_requires_approval_for_create_block():
    decision = DefaultEngineeringPolicy().evaluate(make_operation())

    assert decision.is_allowed
    assert decision.requires_approval
    assert decision.risk == OperationRisk.Medium
    assert decision.denial_reason is None


def test_default_policy_denies_unknown_operation_types():
    class UnknownOperation:
        operation_type = "Unknown"

    decision = DefaultEngineeringPolicy().evaluate(UnknownOperation())

    assert not decision.is_allowed
    assert decision.risk == OperationRisk.Critical


def _transaction(state: TransactionState = TransactionState.Created) -> EngineeringTransaction:
    transaction = EngineeringTransaction.create(
        uuid.uuid4(), "HASH", ProjectContext("project-1", "snapshot-1"), "key", NOW_UTC
    )
    return transaction.with_state(state)


def test_state_machine_allows_the_happy_path():
    machine = TransactionStateMachine()
    transaction = _transaction()

    for state in (
        TransactionState.Validating,
        TransactionState.AwaitingApproval,
    ):
        transaction = machine.transition(transaction, state)
    transaction = machine.transition(
        transaction.with_state(TransactionState.Approved), TransactionState.Executing
    )
    transaction = machine.transition(transaction, TransactionState.ValidatingResult)
    transaction = machine.transition(transaction, TransactionState.Committed)

    assert transaction.state == TransactionState.Committed


def test_state_machine_rejects_illegal_transition():
    with pytest.raises(InvalidTransactionTransitionError):
        TransactionStateMachine().transition(_transaction(), TransactionState.Committed)


def _approval(transaction: EngineeringTransaction, **overrides) -> HumanApproval:
    values = {
        "approval_id": uuid.uuid4(),
        "transaction_id": transaction.transaction_id,
        "operation_hash": transaction.operation_hash,
        "project_snapshot_hash": transaction.project_context.snapshot_hash,
        "approver": IDENTITY,
        "expires_at_utc": NOW_UTC + timedelta(minutes=5),
    }
    values.update(overrides)
    return HumanApproval(**values)


def test_approval_succeeds_when_fully_bound():
    transaction = _transaction(TransactionState.AwaitingApproval)

    result = ApprovalService().approve(transaction, _approval(transaction), IDENTITY, NOW_UTC)

    assert result.is_approved
    assert result.transaction.state == TransactionState.Approved


@pytest.mark.parametrize(
    "overrides,expected",
    [
        ({"operation_hash": "OTHER"}, "Approval operation hash does not match the transaction."),
        (
            {"project_snapshot_hash": "stale"},
            "Approval project snapshot hash does not match the transaction.",
        ),
        ({"transaction_id": uuid.uuid4()}, "Approval transaction ID does not match the transaction."),
        (
            {"approver": AuthenticatedIdentity("other", "client-1")},
            "Approval identity does not match the authenticated identity.",
        ),
        ({"expires_at_utc": NOW_UTC}, "Approval has expired."),
        ({"approval_id": uuid.UUID(int=0)}, "Approval ID is required."),
    ],
)
def test_approval_rejects_any_mismatch(overrides, expected):
    transaction = _transaction(TransactionState.AwaitingApproval)

    result = ApprovalService().approve(
        transaction, _approval(transaction, **overrides), IDENTITY, NOW_UTC
    )

    assert not result.is_approved
    assert expected in result.errors


def test_approval_requires_awaiting_approval_state():
    transaction = _transaction(TransactionState.Created)

    result = ApprovalService().approve(transaction, _approval(transaction), IDENTITY, NOW_UTC)

    assert "Transaction is not awaiting approval." in result.errors
