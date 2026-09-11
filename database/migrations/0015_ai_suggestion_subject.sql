-- 0015 — ai_suggestions.subject_id integrity (slice 20). Flagged since slice 9: the subject is polymorphic
-- (inbound_message | case | tenant), so it could not carry a composite foreign key. This trigger is the
-- equivalent: the subject must exist in the same tenant, or the row is refused (INV-05 by another route).

CREATE OR REPLACE FUNCTION ai_suggestions_subject_exists() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE ok boolean;
BEGIN
  CASE NEW.subject_type
    WHEN 'inbound_message' THEN SELECT EXISTS (SELECT 1 FROM inbound_messages  WHERE tenant_id = NEW.tenant_id AND id = NEW.subject_id) INTO ok;
    WHEN 'case'            THEN SELECT EXISTS (SELECT 1 FROM collection_cases  WHERE tenant_id = NEW.tenant_id AND id = NEW.subject_id) INTO ok;
    WHEN 'tenant'          THEN SELECT (NEW.subject_id = NEW.tenant_id) INTO ok;
    ELSE ok := false;
  END CASE;
  IF NOT ok THEN
    RAISE EXCEPTION 'ai_suggestions: subject % % does not exist in tenant %', NEW.subject_type, NEW.subject_id, NEW.tenant_id
      USING ERRCODE = 'foreign_key_violation', CONSTRAINT = 'ai_suggestions_subject_exists';
  END IF;
  RETURN NEW;
END $$;

CREATE TRIGGER ai_suggestions_subject_exists BEFORE INSERT OR UPDATE OF subject_type, subject_id, tenant_id ON ai_suggestions
  FOR EACH ROW EXECUTE FUNCTION ai_suggestions_subject_exists();
