CREATE PROCEDURE %SCHEMA%.uspDiscardAndReassignOutgoing
    @discards %SCHEMA%.EnvelopeIdList READONLY,
    @reassigned %SCHEMA%.EnvelopeIdList READONLY,
    @ownerId INT

AS

DELETE
FROM %SCHEMA%.wolverine_outgoing_envelopes
WHERE id IN (SELECT id FROM @discards);

UPDATE %SCHEMA%.wolverine_outgoing_envelopes
SET owner_id = @ownerId
WHERE id IN (SELECT id FROM @reassigned);
