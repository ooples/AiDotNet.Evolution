"""Final acceptance requires receipts for the exact executable and audit inputs."""
from program_controls import candidate_hash


def promote(initial, selected, diagnostics, selected_audits, original_audits, *, expected, search_failed=False):
    if not original_audits or len(original_audits) != len(selected_audits) or len(original_audits) != len(expected["audits"]):
        raise ValueError("Missing paired final audits")
    for role,code,rows in (("original",initial,original_audits),("selected",selected,selected_audits)):
        identity = candidate_hash(code)
        for receipt, binding in zip([diagnostics[role],*rows],[expected["diagnostic"],*expected["audits"]]):
            if receipt.get("candidate_hash") != identity or receipt.get("unknown_work") is not False or receipt.get("phase") != "confirmation":
                raise ValueError("Unbound or unreconciled final acceptance receipt")
            if any(not binding.get(k) or receipt.get(k) != binding[k] for k in ("input_sha256","evaluator_sha256")):
                raise ValueError("Receipt does not match planned inputs/evaluator")
    diagnostic_hash = diagnostics["original"].get("input_sha256")
    if not diagnostic_hash or diagnostic_hash != diagnostics["selected"].get("input_sha256"):
        raise ValueError("Unpaired diagnostic inputs")
    audit_hashes = [r.get("input_sha256") for r in original_audits]
    if len(set(audit_hashes)) != len(audit_hashes) or diagnostic_hash in audit_hashes:
        raise ValueError("Repeated or diagnostic-contaminated final audit inputs")
    for original, candidate in zip(original_audits,selected_audits):
        if not original.get("input_sha256") or original["input_sha256"] != candidate.get("input_sha256"):
            raise ValueError("Unpaired final audit inputs")
    # A broken baseline invalidates the comparison, even if the selected program
    # happens to pass. Never report a deployable fallback without actual audits.
    if diagnostics["original"]["status"] != "valid" or any(r["status"] != "valid" for r in original_audits):
        raise ValueError("Original executable failed final acceptance; no deployment")
    fallback = bool(search_failed or initial == selected or diagnostics["selected"]["status"] != "valid" or
                    any(r["status"] != "valid" for r in selected_audits))
    return dict(fallback=fallback, deployed=diagnostics["original" if fallback else "selected"],
                deployed_hash=candidate_hash(initial if fallback else selected))
