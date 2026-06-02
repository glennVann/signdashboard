"use client";

import { DndContext, DragOverlay, PointerSensor, useDraggable, useDroppable, useSensor, useSensors } from "@dnd-kit/core";
import { CSS } from "@dnd-kit/utilities";
import { HubConnectionBuilder, HubConnectionState, LogLevel } from "@microsoft/signalr";
import { ArrowRight, ClipboardList, Download, FileCheck, FileUp, GripVertical, Hammer, Package, Palette, Plus, RefreshCw, Settings, Users, WalletCards, X } from "lucide-react";
import { useEffect, useMemo, useState } from "react";
import { AGENT_DOWNLOAD_URL, apiGet, apiSend, apiUpload, SIGNALR_HUB_URL } from "@/lib/api";

const tabs = [
  { id: "tickets", label: "Tickets", icon: ClipboardList },
  { id: "crm", label: "CRM", icon: Users },
  { id: "inventory", label: "Inventory", icon: Package },
  { id: "quotes", label: "Quotes", icon: WalletCards },
  { id: "jobs", label: "Jobs", icon: Hammer },
  { id: "files", label: "Files", icon: FileUp },
  { id: "settings", label: "Settings", icon: Settings }
];

const ticketColumns = [
  { id: "Pending", label: "Pending" },
  { id: "Design", label: "Design" },
  { id: "Production", label: "Production" },
  { id: "Install", label: "Install" },
  { id: "Done", label: "Done" }
];

const artworkStatuses = [
  "Artwork Needed",
  "In Design",
  "Proof Sent",
  "Revision Needed",
  "Approved",
  "Print Ready"
];

const proofStatuses = [
  "Draft",
  "Sent",
  "Revision Needed",
  "Approved",
  "Rejected"
];

export default function Home() {
  const [activeTab, setActiveTab] = useState("tickets");
  const [activeModal, setActiveModal] = useState(null);
  const [customers, setCustomers] = useState([]);
  const [tickets, setTickets] = useState([]);
  const [inventory, setInventory] = useState([]);
  const [quotes, setQuotes] = useState([]);
  const [jobs, setJobs] = useState([]);
  const [files, setFiles] = useState([]);
  const [artworkFiles, setArtworkFiles] = useState([]);
  const [proofFiles, setProofFiles] = useState([]);
  const [settings, setSettings] = useState({ CUSTOMER_FILES_ROOT: "", STORAGE_MODE: "Direct", LOCAL_AGENT_TOKEN: "" });
  const [storageSummary, setStorageSummary] = useState({ agent: null, queue: { pending: 0, inProgress: 0, completed: 0, failed: 0 }, recentJobs: [] });
  const [dashboard, setDashboard] = useState({ openTickets: 0, activeJobs: 0, lowStock: 0, quoteTotal: 0 });
  const [loading, setLoading] = useState(true);
  const [notice, setNotice] = useState("");
  const [liveStatus, setLiveStatus] = useState("connecting");
  const [lastLiveUpdate, setLastLiveUpdate] = useState(null);
  const [activeTicket, setActiveTicket] = useState(null);
  const [artworkTicket, setArtworkTicket] = useState(null);
  const [proofTicket, setProofTicket] = useState(null);
  const dragSensors = useSensors(useSensor(PointerSensor, { activationConstraint: { distance: 6 } }));

  async function loadData({ silent = false } = {}) {
    if (!silent) {
      setLoading(true);
    }
    const [nextDashboard, nextCustomers, nextTickets, nextInventory, nextQuotes, nextJobs, nextFiles, nextArtworkFiles, nextProofFiles, nextSettings, nextStorageSummary] = await Promise.all([
      apiGet("/dashboard"),
      apiGet("/customers"),
      apiGet("/tickets"),
      apiGet("/inventory"),
      apiGet("/quotes"),
      apiGet("/job-orders"),
      apiGet("/files"),
      apiGet("/artwork"),
      apiGet("/proofs"),
      apiGet("/settings"),
      apiGet("/storage/summary")
    ]);

    setDashboard(nextDashboard);
    setCustomers(nextCustomers);
    setTickets(nextTickets);
    setInventory(nextInventory);
    setQuotes(nextQuotes);
    setJobs(nextJobs);
    setFiles(nextFiles);
    setArtworkFiles(nextArtworkFiles);
    setProofFiles(nextProofFiles);
    setSettings(nextSettings);
    setStorageSummary(nextStorageSummary);
    if (!silent) {
      setLoading(false);
    }
  }

  useEffect(() => {
    loadData().catch(() => {
      setNotice("Connect the ASP.NET API and MariaDB, then refresh.");
      setLoading(false);
    });
  }, []);

  useEffect(() => {
    const connection = new HubConnectionBuilder()
      .withUrl(SIGNALR_HUB_URL)
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build();

    connection.on("dashboardChanged", async (message) => {
      setLiveStatus("live");
      setLastLiveUpdate(message?.changedAt ? new Date(message.changedAt) : new Date());
      try {
        await loadData({ silent: true });
      } catch {
        setLiveStatus("stale");
      }
    });

    connection.onreconnecting(() => setLiveStatus("reconnecting"));
    connection.onreconnected(() => {
      setLiveStatus("live");
      loadData({ silent: true }).catch(() => setLiveStatus("stale"));
    });
    connection.onclose(() => setLiveStatus("offline"));

    connection
      .start()
      .then(() => setLiveStatus(connection.state === HubConnectionState.Connected ? "live" : "connecting"))
      .catch(() => setLiveStatus("offline"));

    return () => {
      connection.stop();
    };
  }, []);

  const customerOptions = useMemo(
    () => customers.map((customer) => <option key={customer.id} value={customer.id}>{customer.companyName}</option>),
    [customers]
  );

  async function submitCustomer(event) {
    event.preventDefault();
    const formElement = event.currentTarget;
    const form = new FormData(formElement);
    await apiSend("/customers", {
      companyName: form.get("companyName"),
      contactName: form.get("contactName"),
      email: form.get("email"),
      phone: form.get("phone"),
      address: form.get("address"),
      notes: form.get("notes")
    });
    formElement.reset();
    setActiveModal(null);
    setNotice("Customer saved.");
    await loadData();
  }

  async function submitTicket(event) {
    event.preventDefault();
    const formElement = event.currentTarget;
    const form = new FormData(formElement);
    await apiSend("/tickets", {
      title: form.get("title"),
      description: form.get("description"),
      status: form.get("status"),
      priority: form.get("priority"),
      assignedTo: form.get("assignedTo"),
      dueDate: form.get("dueDate") || null,
      customerId: numberOrNull(form.get("customerId"))
    });
    formElement.reset();
    setActiveModal(null);
    setNotice("Ticket created.");
    await loadData();
  }

  async function updateTicketStatus(ticket, status) {
    const normalizedStatus = normalizeTicketStatus(ticket.status);
    if (normalizedStatus === status) {
      return;
    }

    if (normalizedStatus === "Design" && status === "Production" && !hasApprovedProof(ticket, proofFiles)) {
      setNotice("Proof approval is required before moving this ticket to Production.");
      return;
    }

    try {
      await apiSend(`/tickets/${ticket.id}`, {
        title: ticket.title,
        description: ticket.description,
        status,
        priority: ticket.priority,
        assignedTo: ticket.assignedTo,
        dueDate: ticket.dueDate || null,
        customerId: ticket.customerId ?? ticket.customer?.id ?? null
      }, "PUT");
      setNotice(`Ticket moved to ${status}.`);
      await loadData({ silent: true });
    } catch (error) {
      setNotice("Proof approval is required before moving this ticket to Production.");
    }
  }

  async function updateTicketPosition(ticket, status, beforeTicketId = null) {
    const normalizedStatus = normalizeTicketStatus(ticket.status);
    if (normalizedStatus === "Design" && status === "Production" && !hasApprovedProof(ticket, proofFiles)) {
      setNotice("Proof approval is required before moving this ticket to Production.");
      return;
    }

    try {
      await apiSend(`/tickets/${ticket.id}/position`, {
        status,
        beforeTicketId
      });
      setNotice(beforeTicketId ? `Ticket prioritized in ${status}.` : `Ticket moved to end of ${status}.`);
      await loadData({ silent: true });
    } catch {
      setNotice("Proof approval is required before moving this ticket to Production.");
    }
  }

  async function handleTicketDragEnd(event) {
    const ticket = activeTicket;
    const overData = event.over?.data?.current;
    const targetStatus = overData?.status ?? event.over?.id;
    const beforeTicketId = overData?.type === "ticket" ? Number(event.over.id) : null;
    setActiveTicket(null);

    if (!ticket || !targetStatus || !ticketColumns.some((column) => column.id === targetStatus)) {
      return;
    }

    if (beforeTicketId === ticket.id) {
      return;
    }

    await updateTicketPosition(ticket, targetStatus, beforeTicketId);
  }

  async function submitInventory(event) {
    event.preventDefault();
    const formElement = event.currentTarget;
    const form = new FormData(formElement);
    await apiSend("/inventory", {
      sku: form.get("sku"),
      name: form.get("name"),
      category: form.get("category"),
      quantityOnHand: Number(form.get("quantityOnHand")),
      reorderPoint: Number(form.get("reorderPoint")),
      unitCost: Number(form.get("unitCost")),
      supplier: form.get("supplier")
    });
    formElement.reset();
    setActiveModal(null);
    setNotice("Inventory item saved.");
    await loadData();
  }

  async function submitQuote(event) {
    event.preventDefault();
    const formElement = event.currentTarget;
    const form = new FormData(formElement);
    await apiSend("/quotes", {
      customerId: Number(form.get("customerId")),
      status: form.get("status"),
      scope: form.get("scope"),
      taxRate: Number(form.get("taxRate")),
      validUntil: form.get("validUntil"),
      lineItems: [{
        description: form.get("lineDescription"),
        quantity: Number(form.get("quantity")),
        unitPrice: Number(form.get("unitPrice"))
      }]
    });
    formElement.reset();
    setActiveModal(null);
    setNotice("Quote generated.");
    await loadData();
  }

  async function submitJob(event) {
    event.preventDefault();
    const formElement = event.currentTarget;
    const form = new FormData(formElement);
    await apiSend("/job-orders", {
      title: form.get("title"),
      status: form.get("status"),
      assignedTo: form.get("assignedTo"),
      productionNotes: form.get("productionNotes"),
      dueDate: form.get("dueDate") || null,
      ticketId: numberOrNull(form.get("ticketId")),
      quoteId: numberOrNull(form.get("quoteId"))
    });
    formElement.reset();
    setActiveModal(null);
    setNotice("Job order generated.");
    await loadData();
  }

  async function submitFile(event) {
    event.preventDefault();
    const formElement = event.currentTarget;
    const form = new FormData(formElement);
    await apiUpload("/files", form);
    formElement.reset();
    setActiveModal(null);
    setNotice("File uploaded and saved.");
    await loadData();
  }

  async function submitArtwork(event) {
    event.preventDefault();
    const formElement = event.currentTarget;
    const form = new FormData(formElement);
    try {
      await apiUpload("/artwork", form);
      formElement.reset();
      setArtworkTicket(null);
      setNotice(settings.STORAGE_MODE === "LocalAgent" ? "Artwork uploaded and queued for local agent sync." : "Artwork uploaded.");
      await loadData();
    } catch (error) {
      setNotice(error.message || "Artwork upload failed.");
    }
  }

  async function submitProof(event) {
    event.preventDefault();
    const formElement = event.currentTarget;
    const form = new FormData(formElement);
    try {
      await apiUpload("/proofs", form);
      formElement.reset();
      setProofTicket(null);
      setNotice(settings.STORAGE_MODE === "LocalAgent" ? "Proof uploaded and queued for local agent sync." : "Proof uploaded.");
      await loadData();
    } catch (error) {
      setNotice(error.message || "Proof upload failed.");
    }
  }

  async function submitSettings(event) {
    event.preventDefault();
    const form = new FormData(event.currentTarget);
    const nextSettings = await apiSend("/settings", {
      customerFilesRoot: form.get("customerFilesRoot") ?? "",
      storageMode: form.get("storageMode") ?? "Direct",
      localAgentToken: form.get("localAgentToken") ?? ""
    }, "PUT");
    setSettings(nextSettings);
    setNotice("Settings saved.");
    await loadData({ silent: true });
  }

  const activeTabLabel = tabs.find((tab) => tab.id === activeTab)?.label ?? "Item";

  return (
    <main className="min-h-screen px-4 py-5 sm:px-6 lg:px-8">
      <div className="mx-auto max-w-7xl">
        <header className="flex flex-col gap-4 border-b border-stone-300 pb-5 lg:flex-row lg:items-end lg:justify-between">
          <div>
            <p className="label">MariaDB database: sign</p>
            <h1 className="mt-1 text-3xl font-bold text-ink sm:text-4xl">Sign Dashboard</h1>
          </div>
          <div className="flex flex-wrap items-center gap-2">
            <LiveBadge status={liveStatus} lastLiveUpdate={lastLiveUpdate} />
            <button className="btn-secondary w-fit" onClick={() => loadData()}>
              <RefreshCw size={16} /> Refresh
            </button>
          </div>
        </header>

        <section className="grid gap-3 py-5 sm:grid-cols-2 lg:grid-cols-4">
          <Metric label="Open Tickets" value={dashboard.openTickets} />
          <Metric label="Active Jobs" value={dashboard.activeJobs} />
          <Metric label="Low Stock Items" value={dashboard.lowStock} />
          <Metric label="Quoted Value" value={`$${dashboard.quoteTotal.toLocaleString()}`} />
        </section>

        {notice ? <div className="mb-4 rounded-md border border-amber-300 bg-amber-50 px-4 py-3 text-sm text-amber-900">{notice}</div> : null}

        <nav className="mb-4 flex gap-2 overflow-x-auto border-b border-stone-300 pb-2">
          {tabs.map((tab) => {
            const Icon = tab.icon;
            return (
              <button
                key={tab.id}
                className={`btn-secondary shrink-0 ${activeTab === tab.id ? "border-ink bg-ink text-white hover:bg-ink" : ""}`}
                onClick={() => setActiveTab(tab.id)}
                title={tab.label}
              >
                <Icon size={16} /> {tab.label}
              </button>
            );
          })}
          {activeTab !== "settings" ? (
            <button className="btn ml-auto shrink-0" onClick={() => setActiveModal(activeTab)}>
              <Plus size={16} /> New {activeTabLabel}
            </button>
          ) : null}
        </nav>

        {loading ? <div className="panel p-6 text-sm text-steel">Loading dashboard data...</div> : null}
        {!loading && activeTab === "tickets" ? (
          <DndContext
            sensors={dragSensors}
            onDragStart={(event) => setActiveTicket(tickets.find((ticket) => String(ticket.id) === String(event.active.id)) ?? null)}
            onDragCancel={() => setActiveTicket(null)}
            onDragEnd={handleTicketDragEnd}
          >
            <TicketsPanel tickets={tickets} proofFiles={proofFiles} onMoveTicket={updateTicketStatus} onOpenArtwork={setArtworkTicket} onOpenProof={setProofTicket} />
            <DragOverlay>{activeTicket ? <TicketCardContent ticket={activeTicket} overlay /> : null}</DragOverlay>
          </DndContext>
        ) : null}
        {!loading && activeTab === "crm" ? <CrmPanel customers={customers} /> : null}
        {!loading && activeTab === "inventory" ? <InventoryPanel inventory={inventory} /> : null}
        {!loading && activeTab === "quotes" ? <QuotesPanel quotes={quotes} /> : null}
        {!loading && activeTab === "jobs" ? <JobsPanel jobs={jobs} /> : null}
        {!loading && activeTab === "files" ? <FilesPanel files={files} /> : null}
        {!loading && activeTab === "settings" ? <SettingsPanel settings={settings} storageSummary={storageSummary} onSubmitSettings={submitSettings} /> : null}
        {activeModal ? (
          <CreateModal
            activeModal={activeModal}
            customers={customerOptions}
            tickets={tickets}
            quotes={quotes}
            jobs={jobs}
            onClose={() => setActiveModal(null)}
            onSubmitCustomer={submitCustomer}
            onSubmitTicket={submitTicket}
            onSubmitInventory={submitInventory}
            onSubmitQuote={submitQuote}
            onSubmitJob={submitJob}
            onSubmitFile={submitFile}
          />
        ) : null}
        {artworkTicket ? (
          <ArtworkModal
            ticket={artworkTicket}
            artworkFiles={artworkFiles.filter((file) => file.ticketId === artworkTicket.id)}
            onClose={() => setArtworkTicket(null)}
            onSubmitArtwork={submitArtwork}
          />
        ) : null}
        {proofTicket ? (
          <ProofModal
            ticket={proofTicket}
            proofFiles={proofFiles.filter((file) => file.ticketId === proofTicket.id)}
            onClose={() => setProofTicket(null)}
            onSubmitProof={submitProof}
          />
        ) : null}
      </div>
    </main>
  );
}

function LiveBadge({ status, lastLiveUpdate }) {
  const statusLabel = {
    connecting: "Connecting",
    live: "Live",
    reconnecting: "Reconnecting",
    stale: "Stale",
    offline: "Offline"
  }[status] ?? "Offline";

  const tone = status === "live"
    ? "border-emerald-300 bg-emerald-50 text-emerald-800"
    : status === "connecting" || status === "reconnecting"
      ? "border-amber-300 bg-amber-50 text-amber-900"
      : "border-rose-300 bg-rose-50 text-rose-800";

  const detail = lastLiveUpdate ? `Updated ${lastLiveUpdate.toLocaleTimeString()}` : "SignalR";

  return (
    <div className={`inline-flex h-10 items-center gap-2 rounded-md border px-3 text-sm font-semibold ${tone}`}>
      <span className="h-2 w-2 rounded-full bg-current" />
      <span>{statusLabel}</span>
      <span className="hidden text-xs font-medium opacity-75 sm:inline">{detail}</span>
    </div>
  );
}

function Metric({ label, value }) {
  return (
    <div className="panel p-4">
      <p className="label">{label}</p>
      <p className="mt-2 text-2xl font-bold text-ink">{value}</p>
    </div>
  );
}

function TicketsPanel({ tickets, proofFiles, onMoveTicket, onOpenArtwork, onOpenProof }) {
  return (
    <section className="overflow-x-auto pb-2">
      <div className="grid min-w-[1280px] grid-cols-5 gap-4">
        {ticketColumns.map((column) => {
          const columnTickets = tickets
            .filter((ticket) => normalizeTicketStatus(ticket.status) === column.id)
            .sort((first, second) => (first.sortOrder ?? 0) - (second.sortOrder ?? 0) || first.id - second.id);
          return (
            <TicketColumn key={column.id} column={column} tickets={columnTickets} proofFiles={proofFiles} onMoveTicket={onMoveTicket} onOpenArtwork={onOpenArtwork} onOpenProof={onOpenProof} />
          );
        })}
      </div>
    </section>
  );
}

function TicketColumn({ column, tickets, proofFiles, onMoveTicket, onOpenArtwork, onOpenProof }) {
  const { isOver, setNodeRef } = useDroppable({
    id: column.id,
    data: { status: column.id }
  });

  return (
    <section ref={setNodeRef} className={`panel min-h-[520px] p-4 transition ${isOver ? "border-mint bg-mint/5" : ""}`}>
      <div className="mb-4 flex items-center justify-between">
        <h2 className="text-sm font-bold text-ink">{column.label}</h2>
        <span className="rounded-md bg-stone-100 px-2 py-1 text-xs font-semibold text-steel">{tickets.length}</span>
      </div>
      <div className="grid gap-4">
        {tickets.map((ticket) => (
          <DraggableTicketCard key={ticket.id} ticket={ticket} proofFiles={proofFiles} onMoveTicket={onMoveTicket} onOpenArtwork={onOpenArtwork} onOpenProof={onOpenProof} />
        ))}
        {tickets.length === 0 ? <p className="rounded-md border border-dashed border-stone-300 p-4 text-sm text-steel">Drop tickets here</p> : null}
      </div>
    </section>
  );
}

function DraggableTicketCard({ ticket, proofFiles, onMoveTicket, onOpenArtwork, onOpenProof }) {
  const { attributes, listeners, setNodeRef, transform, isDragging } = useDraggable({
    id: String(ticket.id),
    data: { ticket }
  });
  const { isOver, setNodeRef: setDropNodeRef } = useDroppable({
    id: String(ticket.id),
    data: {
      type: "ticket",
      status: normalizeTicketStatus(ticket.status),
      ticket
    }
  });
  const style = {
    transform: CSS.Translate.toString(transform)
  };
  function setCardNodeRef(node) {
    setNodeRef(node);
    setDropNodeRef(node);
  }

  return (
    <div ref={setCardNodeRef} style={style} className={isDragging ? "opacity-40" : ""}>
      <TicketCardContent ticket={ticket} proofFiles={proofFiles} onMoveTicket={onMoveTicket} onOpenArtwork={onOpenArtwork} onOpenProof={onOpenProof} dragAttributes={attributes} dragListeners={listeners} isDropTarget={isOver && !isDragging} />
    </div>
  );
}

function TicketCardContent({ ticket, proofFiles = [], onMoveTicket, onOpenArtwork, onOpenProof, dragAttributes, dragListeners, overlay = false, isDropTarget = false }) {
  const due = getDueMeta(ticket.dueDate, ticket.status);
  const currentStatus = normalizeTicketStatus(ticket.status);
  const nextStatus = ticketColumns[ticketColumns.findIndex((column) => column.id === currentStatus) + 1]?.id;
  const proofApproved = hasApprovedProof(ticket, proofFiles);
  const needsProofApproval = currentStatus === "Design" && nextStatus === "Production" && !proofApproved;
  const dragDisabled = currentStatus === "Design" && !proofApproved;

  return (
    <article
      className={`rounded-md border bg-white p-4 shadow-sm transition ${isDropTarget ? "border-mint ring-2 ring-mint/20" : "border-stone-200"} ${overlay ? "w-64 rotate-1 shadow-xl" : ""}`}
    >
      <div className="flex items-start justify-between gap-2">
        <h3 className="text-sm font-bold text-ink">{ticket.title}</h3>
        <span className={`shrink-0 rounded-md px-2 py-1 text-[11px] font-bold ${priorityTone(ticket.priority)}`}>{ticket.priority}</span>
      </div>
      <p className="mt-2 text-xs font-semibold text-mint">{ticket.customer?.companyName ?? "No customer"}</p>
      <p className="mt-1 text-sm text-steel">{ticket.description || "No description"}</p>
      <div className="mt-4 grid gap-2 text-xs text-steel">
        <div className="flex items-center justify-between gap-2">
          <span className="font-semibold">Assigned</span>
          <span>{ticket.assignedTo || "Unassigned"}</span>
        </div>
        <div className="flex items-center justify-between gap-2">
          <span className="font-semibold">Due</span>
          <span className={`rounded-md px-2 py-1 font-bold ${due.tone}`}>{due.label}</span>
        </div>
      </div>
      {!overlay ? (
        <div className="mt-4 flex items-center justify-between gap-2 border-t border-stone-100 pt-3">
          <button
            className="inline-flex h-9 w-9 cursor-grab items-center justify-center rounded-md text-steel transition hover:bg-stone-100 hover:text-ink active:cursor-grabbing disabled:cursor-not-allowed disabled:opacity-35"
            type="button"
            title={dragDisabled ? "Proof approval required" : "Drag ticket"}
            disabled={dragDisabled}
            {...(dragDisabled ? {} : dragAttributes)}
            {...(dragDisabled ? {} : dragListeners)}
          >
            <GripVertical size={30} strokeWidth={2.7} />
          </button>
          <button
            className="inline-flex h-10 w-10 items-center justify-center rounded-md bg-ink text-white transition hover:bg-steel disabled:cursor-not-allowed disabled:opacity-35"
            type="button"
            title={needsProofApproval ? "Proof approval required" : "Move to next stage"}
            disabled={!nextStatus || needsProofApproval}
            onClick={() => nextStatus && onMoveTicket(ticket, nextStatus)}
          >
            <ArrowRight size={31} strokeWidth={2.9} />
          </button>
          <button className="inline-flex h-9 w-9 items-center justify-center rounded-md text-steel transition hover:bg-stone-100 hover:text-ink disabled:cursor-not-allowed disabled:opacity-35" type="button" title="Upload artwork" onClick={() => onOpenArtwork(ticket)}>
            <Palette size={28} strokeWidth={2.7} />
          </button>
          {currentStatus === "Design" ? (
            <button className="inline-flex h-9 w-9 items-center justify-center rounded-md text-steel transition hover:bg-stone-100 hover:text-ink disabled:cursor-not-allowed disabled:opacity-35" type="button" title="Upload proof" onClick={() => onOpenProof(ticket)}>
              <FileCheck size={28} strokeWidth={2.7} />
            </button>
          ) : null}
        </div>
      ) : null}
    </article>
  );
}

function CrmPanel({ customers }) {
  return (
    <section>
      <ListPanel title="CRM Accounts">
        {customers.map((customer) => (
          <Row key={customer.id} title={customer.companyName} meta={`${customer.contactName} / ${customer.phone}`} detail={customer.email} />
        ))}
      </ListPanel>
    </section>
  );
}

function InventoryPanel({ inventory }) {
  return (
    <section>
      <ListPanel title="Stock">
        {inventory.map((item) => (
          <Row key={item.id} title={item.name} meta={`${item.sku} / ${item.category} / $${item.unitCost}`} detail={`${item.quantityOnHand} on hand, reorder at ${item.reorderPoint}`} />
        ))}
      </ListPanel>
    </section>
  );
}

function QuotesPanel({ quotes }) {
  return (
    <section>
      <ListPanel title="Quotes">
        {quotes.map((quote) => (
          <Row key={quote.id} title={`${quote.quoteNumber} / $${quote.total.toLocaleString()}`} meta={`${quote.status} / ${quote.customer?.companyName ?? "No customer"}`} detail={quote.scope} />
        ))}
      </ListPanel>
    </section>
  );
}

function JobsPanel({ jobs }) {
  return (
    <section>
      <ListPanel title="Job Orders">
        {jobs.map((job) => (
          <Row key={job.id} title={`${job.jobNumber} / ${job.title}`} meta={`${job.status} / ${job.assignedTo || "Unassigned"}`} detail={job.dueDate ? `Due ${new Date(job.dueDate).toLocaleDateString()}` : "No due date"} />
        ))}
      </ListPanel>
    </section>
  );
}

function FilesPanel({ files }) {
  return (
    <section>
      <ListPanel title="Saved Files">
        {files.map((file) => (
          <Row key={file.id} title={file.originalFileName} meta={`${file.contentType || "file"} / ${Math.round(file.sizeBytes / 1024)} KB`} detail={new Date(file.uploadedAt).toLocaleString()} />
        ))}
      </ListPanel>
    </section>
  );
}

function SettingsPanel({ settings, storageSummary, onSubmitSettings }) {
  const agent = storageSummary?.agent;
  const queue = storageSummary?.queue ?? {};
  const recentJobs = storageSummary?.recentJobs ?? [];

  return (
    <section className="grid gap-4 lg:grid-cols-[520px_1fr]">
      <form className="panel flex flex-col gap-4 p-5" onSubmit={onSubmitSettings}>
        <div>
          <h2 className="text-lg font-bold text-ink">File Storage</h2>
          <p className="mt-1 text-sm text-steel">Choose direct server storage or an installed local agent for a shop drive, NAS, or fileserver.</p>
        </div>
        <Select name="storageMode" label="Storage Mode" defaultValue={settings.STORAGE_MODE ?? "Direct"}>
          <option value="Direct">Direct from API server</option>
          <option value="LocalAgent">Local Agent</option>
        </Select>
        <Input
          name="customerFilesRoot"
          label="Customer Files Root / Agent Default"
          defaultValue={settings.CUSTOMER_FILES_ROOT ?? ""}
          placeholder="\\\\SERVER\\Share\\Customers"
        />
        <Input
          name="localAgentToken"
          label="Local Agent Token"
          defaultValue={settings.LOCAL_AGENT_TOKEN ?? ""}
          placeholder="Use the same token in the local agent .env"
        />
        <button className="btn w-fit" type="submit">Save Settings</button>
      </form>

      <div className="panel p-5">
        <div className="flex items-start justify-between gap-3">
          <div>
            <h2 className="text-lg font-bold text-ink">Local Agent</h2>
            <p className="mt-1 text-sm text-steel">The agent creates queued customer folders from inside the customer network.</p>
          </div>
          <span className={`rounded-md px-2 py-1 text-xs font-bold ${agent?.status === "Online" ? "bg-emerald-50 text-emerald-800" : "bg-stone-100 text-stone-700"}`}>
            {agent?.status ?? "No agent"}
          </span>
        </div>

        <div className="mt-4 flex flex-wrap gap-2">
          <a className="btn w-fit" href={AGENT_DOWNLOAD_URL}>
            <Download size={16} /> Download Windows Agent
          </a>
          <button
            className="btn-secondary w-fit"
            type="button"
            onClick={() => navigator.clipboard?.writeText(settings.LOCAL_AGENT_TOKEN ?? "")}
          >
            Copy Token
          </button>
        </div>

        <div className="mt-4 grid gap-3 sm:grid-cols-2">
          <StatusTile label="Agent Name" value={agent?.agentName ?? "Not connected"} />
          <StatusTile label="Last Sync" value={agent?.lastSeenAt ? new Date(agent.lastSeenAt).toLocaleString() : "Never"} />
          <StatusTile label="Customer Files Root" value={agent?.customerFilesRoot ?? "Waiting for heartbeat"} />
          <StatusTile label="Queue Status" value={`${queue.pending ?? 0} pending / ${queue.inProgress ?? 0} running / ${queue.failed ?? 0} failed`} />
        </div>

        <div className="mt-5 border-t border-stone-200 pt-4">
          <h3 className="text-sm font-bold text-ink">Recent Storage Jobs</h3>
          <div className="mt-2 divide-y divide-stone-200">
            {recentJobs.map((job) => (
              <article key={job.id} className="grid gap-1 py-3">
                <div className="flex items-start justify-between gap-3">
                  <p className="text-sm font-semibold text-ink">{job.jobType} #{job.id}</p>
                  <span className={`rounded-md px-2 py-1 text-xs font-bold ${storageJobTone(job.status)}`}>{job.status}</span>
                </div>
                <p className="text-xs font-medium text-mint">{job.customerName || "No customer"} / {job.assignedAgentName || "Unassigned"}</p>
                {job.lastError ? <p className="text-sm text-rose-700">{job.lastError}</p> : null}
              </article>
            ))}
            {recentJobs.length === 0 ? <p className="py-3 text-sm text-steel">No storage jobs yet.</p> : null}
          </div>
        </div>

        <div className="mt-5 border-t border-stone-200 pt-4">
          <h3 className="text-sm font-bold text-ink">Path Examples</h3>
          <div className="mt-3 grid gap-3 text-sm text-steel">
            <code className="rounded-md bg-stone-100 p-3 text-ink">\\SERVER-NAME\SignFiles\Customers</code>
            <code className="rounded-md bg-stone-100 p-3 text-ink">\\10.10.4.23\SignFiles\Customers</code>
            <code className="rounded-md bg-stone-100 p-3 text-ink">E:\SignDashboardFiles\Customers</code>
          </div>
        </div>
      </div>
    </section>
  );
}

function StatusTile({ label, value }) {
  return (
    <div className="rounded-md border border-stone-200 bg-white p-3">
      <p className="label">{label}</p>
      <p className="mt-1 break-words text-sm font-semibold text-ink">{value}</p>
    </div>
  );
}

function storageJobTone(status) {
  if (status === "Completed") {
    return "bg-emerald-50 text-emerald-800";
  }

  if (status === "Failed") {
    return "bg-rose-50 text-rose-800";
  }

  if (status === "InProgress") {
    return "bg-sky-50 text-sky-800";
  }

  return "bg-amber-50 text-amber-900";
}

function CreateModal({
  activeModal,
  customers,
  tickets,
  quotes,
  jobs,
  onClose,
  onSubmitCustomer,
  onSubmitTicket,
  onSubmitInventory,
  onSubmitQuote,
  onSubmitJob,
  onSubmitFile
}) {
  const title = {
    tickets: "Create Ticket",
    crm: "Add Customer",
    inventory: "Add Inventory",
    quotes: "Generate Quote",
    jobs: "Generate Job Order",
    files: "Add File From System"
  }[activeModal];

  return (
    <Modal title={title} onClose={onClose}>
      {activeModal === "tickets" ? (
        <FormPanel title={title} onSubmit={onSubmitTicket}>
          <Input name="title" label="Title" required />
          <Textarea name="description" label="Issue / Request" />
          <Select name="customerId" label="Customer"><option value="">Unassigned</option>{customers}</Select>
          <div className="grid grid-cols-2 gap-3">
            <Select name="priority" label="Priority"><option>Normal</option><option>Rush</option><option>Critical</option></Select>
            <Select name="status" label="Status">{ticketColumns.map((column) => <option key={column.id}>{column.id}</option>)}</Select>
          </div>
          <Input name="assignedTo" label="Assigned To" />
          <Input name="dueDate" label="Due Date" type="date" />
        </FormPanel>
      ) : null}

      {activeModal === "crm" ? (
        <FormPanel title={title} onSubmit={onSubmitCustomer}>
          <Input name="companyName" label="Company" required />
          <Input name="contactName" label="Contact" />
          <Input name="email" label="Email" type="email" />
          <Input name="phone" label="Phone" />
          <Input name="address" label="Address" />
          <Textarea name="notes" label="Notes" />
        </FormPanel>
      ) : null}

      {activeModal === "inventory" ? (
        <FormPanel title={title} onSubmit={onSubmitInventory}>
          <Input name="sku" label="SKU" required />
          <Input name="name" label="Item Name" required />
          <Input name="category" label="Category" />
          <div className="grid grid-cols-2 gap-3">
            <Input name="quantityOnHand" label="On Hand" type="number" defaultValue="0" />
            <Input name="reorderPoint" label="Reorder At" type="number" defaultValue="0" />
          </div>
          <Input name="unitCost" label="Unit Cost" type="number" step="0.01" defaultValue="0" />
          <Input name="supplier" label="Supplier" />
        </FormPanel>
      ) : null}

      {activeModal === "quotes" ? (
        <FormPanel title={title} onSubmit={onSubmitQuote}>
          <Select name="customerId" label="Customer" required>{customers}</Select>
          <Textarea name="scope" label="Scope" />
          <Input name="lineDescription" label="Line Item" defaultValue="Custom sign package" />
          <div className="grid grid-cols-2 gap-3">
            <Input name="quantity" label="Qty" type="number" defaultValue="1" />
            <Input name="unitPrice" label="Unit Price" type="number" step="0.01" defaultValue="0" />
          </div>
          <div className="grid grid-cols-2 gap-3">
            <Input name="taxRate" label="Tax Rate" type="number" step="0.001" defaultValue="0.05" />
            <Input name="validUntil" label="Valid Until" type="date" required />
          </div>
          <Select name="status" label="Status"><option>Draft</option><option>Sent</option><option>Approved</option><option>Rejected</option></Select>
        </FormPanel>
      ) : null}

      {activeModal === "jobs" ? (
        <FormPanel title={title} onSubmit={onSubmitJob}>
          <Input name="title" label="Job Title" required />
          <Select name="ticketId" label="Ticket"><option value="">None</option>{tickets.map((ticket) => <option key={ticket.id} value={ticket.id}>{ticket.title}</option>)}</Select>
          <Select name="quoteId" label="Quote"><option value="">None</option>{quotes.map((quote) => <option key={quote.id} value={quote.id}>{quote.quoteNumber}</option>)}</Select>
          <Input name="assignedTo" label="Assigned To" />
          <Input name="dueDate" label="Due Date" type="date" />
          <Select name="status" label="Status"><option>Scheduled</option><option>In Production</option><option>Install Ready</option><option>Completed</option></Select>
          <Textarea name="productionNotes" label="Production Notes" />
        </FormPanel>
      ) : null}

      {activeModal === "files" ? (
        <FormPanel title={title} onSubmit={onSubmitFile} multipart>
          <input className="field" name="file" type="file" required />
          <Select name="customerId" label="Customer"><option value="">None</option>{customers}</Select>
          <Select name="ticketId" label="Ticket"><option value="">None</option>{tickets.map((ticket) => <option key={ticket.id} value={ticket.id}>{ticket.title}</option>)}</Select>
          <Select name="jobOrderId" label="Job Order"><option value="">None</option>{jobs.map((job) => <option key={job.id} value={job.id}>{job.jobNumber}</option>)}</Select>
        </FormPanel>
      ) : null}
    </Modal>
  );
}

function ArtworkModal({ ticket, artworkFiles, onClose, onSubmitArtwork }) {
  return (
    <Modal title={`Artwork / ${ticket.title}`} onClose={onClose}>
      <form className="flex flex-col gap-3" onSubmit={onSubmitArtwork} encType="multipart/form-data">
        <input name="ticketId" type="hidden" value={ticket.id} />
        <input name="customerId" type="hidden" value={ticket.customerId ?? ticket.customer?.id ?? ""} />
        <input className="field" name="file" type="file" accept=".ai,.eps,.pdf,.svg,.psd,.tif,.tiff,.png,.jpg,.jpeg,.cdr" required />
        <div className="grid grid-cols-2 gap-3">
          <Select name="status" label="Artwork Status" defaultValue="In Design">
            {artworkStatuses.map((status) => <option key={status}>{status}</option>)}
          </Select>
          <Input name="uploadedBy" label="Uploaded By" placeholder="Designer name" />
        </div>
        <Textarea name="notes" label="Notes" placeholder="Revision notes, proof details, print-ready notes" />
        <button className="btn" type="submit"><FileUp size={16} /> Upload Artwork</button>
      </form>

      <div className="mt-5 border-t border-stone-200 pt-4">
        <h3 className="text-sm font-bold text-ink">Version History</h3>
        <div className="mt-2 divide-y divide-stone-200">
          {artworkFiles.map((file) => (
            <article key={file.id} className="grid gap-1 py-3">
              <div className="flex items-start justify-between gap-3">
                <div>
                  <p className="text-sm font-semibold text-ink">v{String(file.version).padStart(2, "0")} / {file.originalFileName}</p>
                  <p className="text-xs font-medium text-mint">{file.status} / {file.uploadedBy || "Unknown designer"}</p>
                </div>
                <span className="shrink-0 rounded-md bg-stone-100 px-2 py-1 text-xs font-semibold text-steel">{Math.round(file.sizeBytes / 1024)} KB</span>
              </div>
              <p className="text-xs text-steel">{file.relativePath}</p>
              {file.notes ? <p className="text-sm text-steel">{file.notes}</p> : null}
            </article>
          ))}
          {artworkFiles.length === 0 ? <p className="py-3 text-sm text-steel">No artwork uploaded for this ticket yet.</p> : null}
        </div>
      </div>
    </Modal>
  );
}

function ProofModal({ ticket, proofFiles, onClose, onSubmitProof }) {
  const webUrl = process.env.NEXT_PUBLIC_WEB_URL ?? "http://localhost:3000";
  return (
    <Modal title={`Proofs / ${ticket.title}`} onClose={onClose}>
      <form className="flex flex-col gap-3" onSubmit={onSubmitProof} encType="multipart/form-data">
        <input name="ticketId" type="hidden" value={ticket.id} />
        <input name="customerId" type="hidden" value={ticket.customerId ?? ticket.customer?.id ?? ""} />
        <input className="field" name="file" type="file" accept=".pdf,.png,.jpg,.jpeg" required />
        <div className="grid grid-cols-2 gap-3">
          <Select name="status" label="Proof Status" defaultValue="Draft">
            {proofStatuses.map((status) => <option key={status}>{status}</option>)}
          </Select>
          <Input name="designer" label="Designer" placeholder="Designer name" />
        </div>
        <Input name="sentTo" label="Sent To" placeholder="customer@email.com" />
        <div className="grid grid-cols-2 gap-3">
          <Input name="sentAt" label="Sent Date" type="date" />
          <Input name="approvedAt" label="Approved Date" type="date" />
        </div>
        <Input name="approvedBy" label="Approved By" placeholder="Customer approver" />
        <Textarea name="notes" label="Notes" placeholder="Approval notes, requested changes, or proof comments" />
        <button className="btn" type="submit"><FileUp size={16} /> Upload Proof</button>
      </form>

      <div className="mt-5 border-t border-stone-200 pt-4">
        <h3 className="text-sm font-bold text-ink">Proof History</h3>
        <div className="mt-2 divide-y divide-stone-200">
          {proofFiles.map((file) => (
            <article key={file.id} className="grid gap-1 py-3">
              <div className="flex items-start justify-between gap-3">
                <div>
                  <p className="text-sm font-semibold text-ink">v{String(file.version).padStart(2, "0")} / {file.originalFileName}</p>
                  <p className="text-xs font-medium text-mint">{file.status} / {file.designer || "Unknown designer"}</p>
                </div>
                <span className="shrink-0 rounded-md bg-stone-100 px-2 py-1 text-xs font-semibold text-steel">{Math.round(file.sizeBytes / 1024)} KB</span>
              </div>
              <p className="text-xs text-steel">{file.relativePath}</p>
              {file.reviewToken ? (
                <a className="text-sm font-semibold text-mint hover:underline" href={`${webUrl}/proofs/review/${file.reviewToken}`} target="_blank">
                  Open customer review link
                </a>
              ) : null}
              {file.sentTo ? <p className="text-sm text-steel">Sent to {file.sentTo}</p> : null}
              {file.approvedBy ? <p className="text-sm text-steel">Approved by {file.approvedBy}</p> : null}
              {file.notes ? <p className="text-sm text-steel">{file.notes}</p> : null}
            </article>
          ))}
          {proofFiles.length === 0 ? <p className="py-3 text-sm text-steel">No proofs uploaded for this ticket yet.</p> : null}
        </div>
      </div>
    </Modal>
  );
}

function Modal({ title, children, onClose }) {
  return (
    <div className="fixed inset-0 z-50 flex items-start justify-center bg-ink/35 px-4 py-8 backdrop-blur-sm">
      <div className="max-h-[calc(100vh-4rem)] w-full max-w-xl overflow-y-auto rounded-lg bg-white shadow-xl">
        <div className="sticky top-0 z-10 flex items-center justify-between border-b border-stone-200 bg-white px-4 py-3">
          <h2 className="text-lg font-bold text-ink">{title}</h2>
          <button className="btn-secondary h-9 w-9 px-0" type="button" onClick={onClose} title="Close">
            <X size={16} />
          </button>
        </div>
        <div className="p-4">{children}</div>
      </div>
    </div>
  );
}

function FormPanel({ title, children, onSubmit, multipart }) {
  return (
    <form className="flex flex-col gap-3" onSubmit={onSubmit} encType={multipart ? "multipart/form-data" : undefined}>
      {children}
      <button className="btn mt-1" type="submit"><Plus size={16} /> Save</button>
    </form>
  );
}

function ListPanel({ title, children }) {
  return (
    <div className="panel min-h-[360px] p-4">
      <h2 className="text-lg font-bold text-ink">{title}</h2>
      <div className="mt-3 divide-y divide-stone-200">{children}</div>
    </div>
  );
}

function Row({ title, meta, detail }) {
  return (
    <article className="grid gap-1 py-3">
      <h3 className="text-sm font-semibold text-ink">{title}</h3>
      <p className="text-xs font-medium text-mint">{meta}</p>
      <p className="text-sm text-steel">{detail}</p>
    </article>
  );
}

function Input({ label, ...inputProps }) {
  return <label className="grid gap-1"><span className="label">{label}</span><input className="field" {...inputProps} /></label>;
}

function Textarea({ label, ...textareaProps }) {
  return <label className="grid gap-1"><span className="label">{label}</span><textarea className="field min-h-24 resize-y" {...textareaProps} /></label>;
}

function Select({ label, children, ...selectProps }) {
  return <label className="grid gap-1"><span className="label">{label}</span><select className="field" {...selectProps}>{children}</select></label>;
}

function numberOrNull(value) {
  const parsed = Number(value);
  return Number.isFinite(parsed) && parsed > 0 ? parsed : null;
}

function normalizeTicketStatus(status) {
  const normalized = {
    Open: "Pending",
    Waiting: "Pending",
    "In Review": "Design",
    Converted: "Production",
    "In Progress": "Production",
    "Install Ready": "Install",
    Closed: "Done",
    Completed: "Done"
  }[status];

  return normalized ?? (ticketColumns.some((column) => column.id === status) ? status : "Pending");
}

function getDueMeta(dueDate, status) {
  if (normalizeTicketStatus(status) === "Done") {
    return { label: "Done", tone: "bg-emerald-50 text-emerald-800" };
  }

  if (!dueDate) {
    return { label: "No date", tone: "bg-stone-100 text-stone-600" };
  }

  const today = new Date();
  today.setHours(0, 0, 0, 0);
  const due = new Date(dueDate);
  due.setHours(0, 0, 0, 0);
  const days = Math.round((due.getTime() - today.getTime()) / 86400000);

  if (days < 0) {
    return { label: "Overdue", tone: "bg-rose-50 text-rose-800" };
  }

  if (days === 0) {
    return { label: "Today", tone: "bg-amber-50 text-amber-900" };
  }

  if (days === 1) {
    return { label: "Tomorrow", tone: "bg-amber-50 text-amber-900" };
  }

  return { label: due.toLocaleDateString(), tone: "bg-sky-50 text-sky-800" };
}

function priorityTone(priority) {
  if (priority === "Critical") {
    return "bg-rose-50 text-rose-800";
  }

  if (priority === "Rush") {
    return "bg-amber-50 text-amber-900";
  }

  return "bg-stone-100 text-stone-700";
}

function hasApprovedProof(ticket, proofFiles) {
  const latestProof = proofFiles
    .filter((file) => file.ticketId === ticket.id)
    .sort((first, second) => second.version - first.version)[0];

  return latestProof?.status === "Approved";
}
