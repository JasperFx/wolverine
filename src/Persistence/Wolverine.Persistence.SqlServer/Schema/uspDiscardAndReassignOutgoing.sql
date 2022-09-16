CREATE PROCEDURE %SCHEMA%.uspDiscardAndReassignOutgoing
    @DISCARDS %SCHEMA%.EnvelopeIdList READONLY,
    @REASSIGNED %SCHEMA%.EnvelopeIdList READONLY,
    @OWNERID INT

AS

DELETE
FROM %SCHEMA%.jasper_outgoing_envelopes
WHERE id IN (SELECT ID FROM @DISCARDS);

UPDATE %SCHEMA%.jasper_outgoing_envelopes
SET owner_id = @OWNERID
WHERE ID IN (SELECT ID FROM @REASSIGNED);
