"use client";

import { CheckCircle2, FileText, RefreshCw, XCircle } from "lucide-react";
import { useEffect, useState } from "react";
import { API_BASE_URL } from "@/lib/api";

export default function ProofReviewPage({ params }) {
  const { token } = params;
  const [proof, setProof] = useState(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const [comment, setComment] = useState("");
  const [approvedBy, setApprovedBy] = useState("");
  const [submitting, setSubmitting] = useState(false);
  const [submittedDecision, setSubmittedDecision] = useState("");

  useEffect(() => {
    async function loadProof() {
      try {
        const response = await fetch(`${API_BASE_URL}/proofs/review/${token}`, { cache: "no-store" });
        if (!response.ok) {
          throw new Error("Proof link is invalid or expired.");
        }
        setProof(await response.json());
      } catch (loadError) {
        setError(loadError.message);
      } finally {
        setLoading(false);
      }
    }

    loadProof();
  }, [token]);

  async function submitDecision(decision) {
    setSubmitting(true);
    setError("");
    try {
      const response = await fetch(`${API_BASE_URL}/proofs/review/${token}`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ decision, comment, approvedBy })
      });

      if (!response.ok) {
        throw new Error("Unable to submit your proof response.");
      }

      setSubmittedDecision(decision);
      setProof({ ...proof, status: decision, customerDecision: decision, customerComment: comment, approvedBy });
    } catch (submitError) {
      setError(submitError.message);
    } finally {
      setSubmitting(false);
    }
  }

  if (loading) {
    return (
      <main className="flex min-h-screen items-center justify-center px-4">
        <div className="panel flex items-center gap-3 p-5 text-sm text-steel">
          <RefreshCw className="animate-spin" size={18} /> Loading proof...
        </div>
      </main>
    );
  }

  if (error && !proof) {
    return (
      <main className="flex min-h-screen items-center justify-center px-4">
        <div className="panel max-w-md p-6">
          <h1 className="text-xl font-bold text-ink">Proof Link Unavailable</h1>
          <p className="mt-2 text-sm text-steel">{error}</p>
        </div>
      </main>
    );
  }

  const fileUrl = `${API_BASE_URL.replace(/\/api\/?$/, "")}${proof.downloadUrl}`;
  const canPreview = proof.contentType?.startsWith("image/") || proof.contentType === "application/pdf";

  return (
    <main className="min-h-screen px-4 py-6 sm:px-6 lg:px-8">
      <div className="mx-auto grid max-w-6xl gap-5 lg:grid-cols-[1fr_380px]">
        <section className="panel min-h-[70vh] overflow-hidden">
          <div className="border-b border-stone-200 p-4">
            <p className="label">Proof Review</p>
            <h1 className="mt-1 text-2xl font-bold text-ink">{proof.ticket?.title ?? proof.jobOrder?.title ?? proof.originalFileName}</h1>
            <p className="mt-1 text-sm text-steel">{proof.customer?.companyName} / v{String(proof.version).padStart(2, "0")}</p>
          </div>
          <div className="h-[72vh] bg-stone-100">
            {canPreview ? (
              <iframe className="h-full w-full" src={fileUrl} title={proof.originalFileName} />
            ) : (
              <div className="flex h-full flex-col items-center justify-center gap-3 text-steel">
                <FileText size={44} />
                <p className="text-sm font-semibold">{proof.originalFileName}</p>
                <a className="btn" href={fileUrl}>Download Proof</a>
              </div>
            )}
          </div>
        </section>

        <aside className="panel h-fit p-5">
          <div className="flex items-center justify-between gap-3">
            <div>
              <p className="label">Current Status</p>
              <p className="mt-1 text-lg font-bold text-ink">{proof.status}</p>
            </div>
            <span className="rounded-md bg-stone-100 px-3 py-2 text-sm font-bold text-steel">v{String(proof.version).padStart(2, "0")}</span>
          </div>

          {proof.notes ? <p className="mt-4 rounded-md bg-stone-50 p-3 text-sm text-steel">{proof.notes}</p> : null}
          {submittedDecision ? (
            <div className="mt-4 rounded-md border border-mint/30 bg-mint/10 p-3 text-sm font-semibold text-mint">
              Response submitted: {submittedDecision}
            </div>
          ) : null}
          {error ? <div className="mt-4 rounded-md border border-rose-300 bg-rose-50 p-3 text-sm text-rose-800">{error}</div> : null}

          <label className="mt-4 grid gap-1">
            <span className="label">Your Name</span>
            <input className="field" value={approvedBy} onChange={(event) => setApprovedBy(event.target.value)} placeholder="Name" />
          </label>
          <label className="mt-3 grid gap-1">
            <span className="label">Notes</span>
            <textarea className="field min-h-32 resize-y" value={comment} onChange={(event) => setComment(event.target.value)} placeholder="Add approval notes or revision instructions" />
          </label>

          <div className="mt-4 grid gap-3">
            <button className="btn" disabled={submitting} onClick={() => submitDecision("Approved")}>
              <CheckCircle2 size={18} /> Approve Proof
            </button>
            <button className="btn-secondary border-signal/30 text-signal hover:bg-signal/10" disabled={submitting} onClick={() => submitDecision("Revision Needed")}>
              <XCircle size={18} /> Request Revision
            </button>
          </div>
        </aside>
      </div>
    </main>
  );
}
