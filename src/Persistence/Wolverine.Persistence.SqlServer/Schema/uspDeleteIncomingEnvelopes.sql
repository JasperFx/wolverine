CREATE PROCEDURE %SCHEMA%.uspDeleteIncomingEnvelopes
    @IDLIST %SCHEMA%.EnvelopeIdList READONLY
AS

DELETE
FROM %SCHEMA%.jasper_incoming_envelopes
WHERE id IN (SELECT ID FROM @IDLIST);
