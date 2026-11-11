"use client";

import { DndContext, DragOverlay, PointerSensor, useDraggable, useDroppable, useSensor, useSensors } from "@dnd-kit/core";
import { CSS } from "@dnd-kit/utilities";
import { HubConnectionBuilder, HubConnectionState, LogLevel } from "@microsoft/signalr";
import { ArrowRight, ClipboardList, Download, FileCheck, FileUp, GripVertical, Hammer, Package, Palette, Plus, RefreshCw, Settings, Users, WalletCards, X } from "lucide-react";
import { useEffect, useMemo, useState } from "react";
import { AGENT_DOWNLOAD_URL, apiGet, apiSend, apiUpload, SIGNALR_HUB_URL } from "@/lib/api";

const tabs = [
  { id: "crm", label: "CRM", icon: Users },
  { id: "quotes", label: "Quotes", icon: WalletCards },
  { id: "jobs", label: "Jobs", icon: Hammer },
  { id: "tickets", label: "Tickets", icon: ClipboardList },
  { id: "files", label: "Files", icon: FileUp },
  { id: "inventory", label: "Inventory", icon: Package },
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

const leadStatuses = ["New", "Qualified", "Quoted", "Won", "Lost"];
const opportunityStages = ["Lead", "Survey", "Quoted", "Negotiation", "Won", "Lost"];
const activityTypes = ["Call", "Email", "Meeting", "Site Survey", "Follow-up", "Task"];
const activityStatuses = ["Open", "Completed"];
const jobStatuses = ["Artwork", "Proofing", "Approved for Production", "In Production", "Install Ready", "Completed"];
const fileCategories = ["General", "Production", "DXF", "Fabrication PDF", "Wiring Layout", "Spec Sheet"];
const CRM_PAGE_SIZE = 10;

function normalizeJobStatus(status) {
  return status === "Design" ? "Artwork" : (status || "Artwork");
}

function createBlankAccountForm() {
  return {
    companyName: "",
    contactName: "",
    email: "",
    phone: "",
    address: "",
    notes: ""
  };
}

function createBlankQuoteLineItem(item = {}) {
  return {
    description: item.description ?? "",
    quantity: item.quantity ?? 1,
    unitPrice: item.unitPrice ?? 0
  };
}

function createBlankJobForm(item = {}) {
  return {
    title: item.title ?? "",
    ticketId: item.ticketId ? String(item.ticketId) : "",
    quoteId: item.quoteId ? String(item.quoteId) : "",
    assignedTo: item.assignedTo ?? "",
    dueDate: item.dueDate ? String(item.dueDate).slice(0, 10) : "",
    status: normalizeJobStatus(item.status),
    productionNotes: item.productionNotes ?? "",
    dxfReady: Boolean(item.dxfReady),
    bendFileReady: Boolean(item.bendFileReady),
    backsReady: Boolean(item.backsReady),
    facesReady: Boolean(item.facesReady),
    ledLayoutReady: Boolean(item.ledLayoutReady)
  };
}

function isProductionFileCategory(category) {
  return ["Production", "DXF", "Fabrication PDF", "Wiring Layout", "Spec Sheet"].includes(category);
}

function buildJobTitleFromSelection({ quote, ticket }) {
  if (quote) {
    const parts = [
      quote.customer?.companyName,
      quote.opportunity?.title,
      quote.projectType
    ].filter(Boolean);

    if (parts.length > 0) {
      return parts.join(" / ");
    }

    return quote.quoteNumber || "";
  }

  if (ticket) {
    const parts = [
      ticket.customer?.companyName,
      ticket.title
    ].filter(Boolean);

    return parts.join(" / ");
  }

  return "";
}

export default function Home() {
  const [activeTab, setActiveTab] = useState(tabs[0].id);
  const [activeModal, setActiveModal] = useState(null);
  const [customers, setCustomers] = useState([]);
  const [crmLeads, setCrmLeads] = useState([]);
  const [activeCustomerId, setActiveCustomerId] = useState(null);
  const [crmDetail, setCrmDetail] = useState(null);
  const [crmLoading, setCrmLoading] = useState(false);
  const [quoteTemplates, setQuoteTemplates] = useState([]);
  const [jobWorkers, setJobWorkers] = useState([]);
  const [quoteCustomerId, setQuoteCustomerId] = useState("");
  const [quoteProjectType, setQuoteProjectType] = useState("");
  const [quoteOpportunityId, setQuoteOpportunityId] = useState("");
  const [quoteLineItems, setQuoteLineItems] = useState([createBlankQuoteLineItem()]);
  const [jobTitle, setJobTitle] = useState("");
  const [jobQuoteId, setJobQuoteId] = useState("");
  const [jobTicketId, setJobTicketId] = useState("");
  const [quoteOpportunities, setQuoteOpportunities] = useState([]);
  const [quoteOpportunitiesLoading, setQuoteOpportunitiesLoading] = useState(false);
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
  const [proofJob, setProofJob] = useState(null);
  const dragSensors = useSensors(useSensor(PointerSensor, { activationConstraint: { distance: 6 } }));

  async function loadCrmDetail(customerId) {
    if (!customerId) {
      setCrmDetail(null);
      return;
    }

    setCrmLoading(true);
    try {
      setCrmDetail(await apiGet(`/customers/${customerId}/crm`));
    } finally {
      setCrmLoading(false);
    }
  }

  async function loadData({ silent = false } = {}) {
    if (!silent) {
      setLoading(true);
    }
    const [nextDashboard, nextCustomers, nextTickets, nextInventory, nextQuotes, nextJobs, nextFiles, nextArtworkFiles, nextProofFiles, nextSettings, nextStorageSummary, nextCrmLeads, nextJobWorkers] = await Promise.all([
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
      apiGet("/storage/summary"),
      apiGet("/leads"),
      apiGet("/job-workers")
    ]);
    const nextSelectedCustomerId = nextCustomers.some((customer) => customer.id === activeCustomerId)
      ? activeCustomerId
      : (nextCustomers[0]?.id ?? null);
    let nextCrmDetail = null;
    if (nextSelectedCustomerId) {
      setCrmLoading(true);
      try {
        nextCrmDetail = await apiGet(`/customers/${nextSelectedCustomerId}/crm`);
      } finally {
        setCrmLoading(false);
      }
    } else {
      setCrmLoading(false);
    }

    setDashboard(nextDashboard);
    setCustomers(nextCustomers);
    setCrmLeads(nextCrmLeads);
    setActiveCustomerId(nextSelectedCustomerId);
    setCrmDetail(nextCrmDetail);
    setTickets(nextTickets);
    setInventory(nextInventory);
    setQuotes(nextQuotes);
    setJobs(nextJobs);
    setFiles(nextFiles);
    setArtworkFiles(nextArtworkFiles);
    setProofFiles(nextProofFiles);
    setSettings(nextSettings);
    setStorageSummary(nextStorageSummary);
    setJobWorkers(nextJobWorkers);
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
  const projectTypeOptions = useMemo(
    () => quoteTemplates.map((template) => (
      <option key={template.projectType} value={template.projectType}>{template.projectType}</option>
    )),
    [quoteTemplates]
  );
  const quoteOpportunityOptions = useMemo(
    () => quoteOpportunities.map((opportunity) => (
      <option key={opportunity.id} value={opportunity.id}>
        {opportunity.title} ({opportunity.projectType || "Unspecified"} / {opportunity.stage})
      </option>
    )),
    [quoteOpportunities]
  );
  const quoteTemplateMap = useMemo(
    () => Object.fromEntries(quoteTemplates.map((template) => [template.projectType, template])),
    [quoteTemplates]
  );

  useEffect(() => {
    apiGet("/quote-templates")
      .then(setQuoteTemplates)
      .catch(() => setQuoteTemplates([]));
  }, []);

  useEffect(() => {
    if (activeModal !== "quotes") {
      return;
    }

    setQuoteCustomerId(activeCustomerId ? String(activeCustomerId) : "");
    setQuoteProjectType("");
    setQuoteOpportunityId("");
    setQuoteLineItems([createBlankQuoteLineItem()]);
  }, [activeCustomerId, activeModal]);

  useEffect(() => {
    if (activeModal !== "quotes") {
      return;
    }

    if (!quoteCustomerId) {
      setQuoteOpportunities([]);
      setQuoteOpportunitiesLoading(false);
      setQuoteOpportunityId("");
      return;
    }

    let cancelled = false;
    setQuoteOpportunitiesLoading(true);

    apiGet(`/customers/${quoteCustomerId}/opportunities`)
      .then((items) => {
        if (cancelled) {
          return;
        }

        setQuoteOpportunities(items);
        setQuoteOpportunityId((current) => items.some((item) => String(item.id) === current) ? current : "");
      })
      .catch(() => {
        if (cancelled) {
          return;
        }

        setQuoteOpportunities([]);
        setQuoteOpportunityId("");
      })
      .finally(() => {
        if (!cancelled) {
          setQuoteOpportunitiesLoading(false);
        }
      });

    return () => {
      cancelled = true;
    };
  }, [activeModal, quoteCustomerId]);

  useEffect(() => {
    if (activeModal !== "jobs") {
      return;
    }

    setJobTitle("");
    setJobQuoteId("");
    setJobTicketId("");
  }, [activeModal]);

  useEffect(() => {
    if (activeModal !== "quotes") {
      return;
    }

    const selectedOpportunity = quoteOpportunities.find((item) => String(item.id) === quoteOpportunityId);
    if (!selectedOpportunity?.projectType) {
      return;
    }

    const nextProjectType = selectedOpportunity.projectType;
    setQuoteProjectType(nextProjectType);

    const template = quoteTemplateMap[nextProjectType];
    setQuoteLineItems(
      template?.lines?.length
        ? template.lines.map((line) => createBlankQuoteLineItem(line))
        : [createBlankQuoteLineItem()]
    );
  }, [activeModal, quoteOpportunityId, quoteOpportunities, quoteTemplateMap]);

  async function saveAccount(payload, customerId = null) {
    const saved = customerId
      ? await apiSend(`/customers/${customerId}`, payload, "PUT")
      : await apiSend("/customers", payload);
    setNotice(customerId ? "Account updated." : "Account created.");
    await loadData();
    return saved;
  }

  async function archiveAccount(customerId) {
    await apiSend(`/customers/${customerId}/archive`, null, "PUT");
    setNotice("Account archived.");
    await loadData();
  }

  async function saveJob(payload, jobId = null) {
    const saved = jobId
      ? await apiSend(`/job-orders/${jobId}`, payload, "PUT")
      : await apiSend("/job-orders", payload);
    setNotice(jobId ? "Job order updated." : "Job order generated.");
    await loadData();
    return saved;
  }

  async function deleteJobOrder(jobId) {
    await apiSend(`/job-orders/${jobId}`, null, "DELETE");
    setNotice("Job order deleted.");
    await loadData();
  }

  function applyProjectTypeTemplate(projectType) {
    setQuoteProjectType(projectType);
    const template = quoteTemplateMap[projectType];
    setQuoteLineItems(
      template?.lines?.length
        ? template.lines.map((line) => createBlankQuoteLineItem(line))
        : [createBlankQuoteLineItem()]
    );
  }

  function updateQuoteLineItem(index, field, value) {
    setQuoteLineItems((current) => current.map((item, itemIndex) => {
      if (itemIndex !== index) {
        return item;
      }

      return {
        ...item,
        [field]: field === "description" ? value : Number(value || 0)
      };
    }));
  }

  function addQuoteLineItem() {
    setQuoteLineItems((current) => [...current, createBlankQuoteLineItem()]);
  }

  function removeQuoteLineItem(index) {
    setQuoteLineItems((current) => current.length > 1 ? current.filter((_, itemIndex) => itemIndex !== index) : [createBlankQuoteLineItem()]);
  }

  function applyJobTitleFromSelection({ nextQuoteId = jobQuoteId, nextTicketId = jobTicketId } = {}) {
    const selectedQuote = quotes.find((quote) => String(quote.id) === String(nextQuoteId));
    const selectedTicket = tickets.find((ticket) => String(ticket.id) === String(nextTicketId));
    setJobTitle(buildJobTitleFromSelection({ quote: selectedQuote, ticket: selectedTicket }));
  }

  async function submitCustomer(event) {
    event.preventDefault();
    const formElement = event.currentTarget;
    const form = new FormData(formElement);
    await saveAccount({
      companyName: form.get("companyName"),
      contactName: form.get("contactName"),
      email: form.get("email"),
      phone: form.get("phone"),
      address: form.get("address"),
      notes: form.get("notes")
    });
    formElement.reset();
    setActiveModal(null);
  }

  async function submitLead(event) {
    event.preventDefault();
    const formElement = event.currentTarget;
    const form = new FormData(formElement);
    await apiSend("/leads", {
      companyName: form.get("companyName"),
      contactName: form.get("contactName"),
      email: form.get("email"),
      phone: form.get("phone"),
      source: form.get("source"),
      status: form.get("status"),
      estimatedValue: optionalNumber(form.get("estimatedValue")),
      notes: form.get("notes")
    });
    formElement.reset();
    setNotice("Lead saved.");
    await loadData();
  }

  async function submitContact(event) {
    event.preventDefault();
    if (!activeCustomerId) {
      return;
    }

    const formElement = event.currentTarget;
    const form = new FormData(formElement);
    await apiSend(`/customers/${activeCustomerId}/contacts`, {
      firstName: form.get("firstName"),
      lastName: form.get("lastName"),
      title: form.get("title"),
      email: form.get("email"),
      phone: form.get("phone"),
      isPrimaryContact: form.get("isPrimaryContact") === "on",
      notes: form.get("notes")
    });
    formElement.reset();
    setNotice("Contact added.");
    await loadData({ silent: true });
  }

  async function submitOpportunity(event) {
    event.preventDefault();
    if (!activeCustomerId) {
      return;
    }

    const formElement = event.currentTarget;
    const form = new FormData(formElement);
    await apiSend(`/customers/${activeCustomerId}/opportunities`, {
      title: form.get("title"),
      projectType: form.get("projectType"),
      stage: form.get("stage"),
      estimatedValue: Number(form.get("estimatedValue") || 0),
      probability: Number(form.get("probability") || 0),
      source: form.get("source"),
      expectedCloseDate: form.get("expectedCloseDate") || null,
      notes: form.get("notes")
    });
    formElement.reset();
    setNotice("Opportunity added.");
    await loadData({ silent: true });
  }

  async function submitActivity(event) {
    event.preventDefault();
    if (!activeCustomerId) {
      return;
    }

    const formElement = event.currentTarget;
    const form = new FormData(formElement);
    await apiSend(`/customers/${activeCustomerId}/activities`, {
      type: form.get("type"),
      subject: form.get("subject"),
      notes: form.get("notes"),
      assignedTo: form.get("assignedTo"),
      status: form.get("status"),
      dueAt: form.get("dueAt") || null,
      opportunityId: numberOrNull(form.get("opportunityId"))
    });
    formElement.reset();
    setNotice("Activity logged.");
    await loadData({ silent: true });
  }

  async function selectCrmAccount(customerId) {
    setActiveCustomerId(customerId);
    await loadCrmDetail(customerId);
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
    const normalizedLineItems = quoteLineItems
      .map((item) => ({
        description: item.description.trim(),
        quantity: Math.max(1, Number(item.quantity || 0)),
        unitPrice: Math.max(0, Number(item.unitPrice || 0))
      }))
      .filter((item) => item.description);

    if (normalizedLineItems.length === 0) {
      setNotice("Add at least one line item before generating the quote.");
      return;
    }

    await apiSend("/quotes", {
      customerId: Number(form.get("customerId")),
      opportunityId: numberOrNull(form.get("opportunityId")),
      projectType: quoteProjectType,
      status: form.get("status"),
      scope: form.get("scope"),
      taxRate: Number(form.get("taxRate")),
      validUntil: form.get("validUntil"),
      lineItems: normalizedLineItems
    });
    formElement.reset();
    setQuoteCustomerId(activeCustomerId ? String(activeCustomerId) : "");
    setQuoteProjectType("");
    setQuoteOpportunityId("");
    setQuoteOpportunities([]);
    setQuoteLineItems([createBlankQuoteLineItem()]);
    setActiveModal(null);
    setNotice("Quote generated.");
    await loadData();
  }

  async function submitJob(event) {
    event.preventDefault();
    const formElement = event.currentTarget;
    const form = new FormData(formElement);
    await saveJob({
      title: form.get("title"),
      status: form.get("status"),
      assignedTo: form.get("assignedTo"),
      productionNotes: form.get("productionNotes"),
      dxfReady: form.get("dxfReady") === "on",
      bendFileReady: form.get("bendFileReady") === "on",
      backsReady: form.get("backsReady") === "on",
      facesReady: form.get("facesReady") === "on",
      ledLayoutReady: form.get("ledLayoutReady") === "on",
      dueDate: form.get("dueDate") || null,
      ticketId: numberOrNull(form.get("ticketId")),
      quoteId: numberOrNull(form.get("quoteId"))
    });
    formElement.reset();
    setJobTitle("");
    setJobQuoteId("");
    setJobTicketId("");
    setActiveModal(null);
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
      setNotice(settings.STORAGE_MODE === "LocalAgent" ? "Artwork uploaded and draft proof created. Both were queued for local agent sync." : "Artwork uploaded and draft proof created.");
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
      setProofJob(null);
      setNotice(settings.STORAGE_MODE === "LocalAgent" ? "Proof uploaded and queued for local agent sync." : "Proof uploaded.");
      await loadData();
    } catch (error) {
      setNotice(error.message || "Proof upload failed.");
    }
  }

  async function openProofReview(proof) {
    if (!proof?.id) {
      return;
    }

    const result = await apiSend(`/proofs/${proof.id}/create-review-link`, null, "POST");
    window.open(result.reviewUrl, "_blank", "noopener,noreferrer");
    setNotice("Proof review link opened.");
    await loadData({ silent: true });
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
  const createItemLabel = activeTab === "crm" ? "Account" : activeTabLabel;

  return (
    <main className="min-h-screen px-4 py-5 sm:px-6 lg:px-8 xl:px-10">
      <div className="mx-auto w-full max-w-[1920px]">
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

        {notice ? <div className="mb-4 rounded-md border border-signal/30 bg-signal/10 px-4 py-3 text-sm text-signal">{notice}</div> : null}

        <nav className="mb-4 flex gap-2 overflow-x-auto border-b border-stone-300 pb-2">
          {tabs.map((tab) => {
            const Icon = tab.icon;
            return (
              <button
                key={tab.id}
                className={`btn-secondary shrink-0 ${activeTab === tab.id ? "border-mint bg-mint text-white hover:border-mint hover:bg-blue-700 hover:text-white" : ""}`}
                onClick={() => setActiveTab(tab.id)}
                title={tab.label}
              >
                <Icon size={16} /> {tab.label}
              </button>
            );
          })}
          {activeTab !== "settings" && activeTab !== "crm" ? (
            <button className="btn ml-auto shrink-0" onClick={() => setActiveModal(activeTab)}>
              <Plus size={16} /> New {createItemLabel}
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
        {!loading && activeTab === "crm" ? (
          <CrmPanel
            customers={customers}
            crmLeads={crmLeads}
            crmDetail={crmDetail}
            activeCustomerId={activeCustomerId}
            crmLoading={crmLoading}
            projectTypeOptions={projectTypeOptions}
            onSelectCustomer={selectCrmAccount}
            onSaveAccount={saveAccount}
            onArchiveAccount={archiveAccount}
            onSubmitLead={submitLead}
            onSubmitContact={submitContact}
            onSubmitOpportunity={submitOpportunity}
            onSubmitActivity={submitActivity}
          />
        ) : null}
        {!loading && activeTab === "inventory" ? <InventoryPanel inventory={inventory} /> : null}
        {!loading && activeTab === "quotes" ? <QuotesPanel quotes={quotes} /> : null}
        {!loading && activeTab === "jobs" ? (
          <JobsPanel
            jobs={jobs}
            files={files}
            tickets={tickets}
            quotes={quotes}
            jobWorkers={jobWorkers}
            proofFiles={proofFiles}
            onOpenProof={setProofJob}
            onOpenProofReview={openProofReview}
            onSaveJob={saveJob}
            onDeleteJob={deleteJobOrder}
          />
        ) : null}
        {!loading && activeTab === "files" ? <FilesPanel files={files} /> : null}
        {!loading && activeTab === "settings" ? <SettingsPanel settings={settings} storageSummary={storageSummary} onSubmitSettings={submitSettings} /> : null}
        {activeModal ? (
          <CreateModal
            activeModal={activeModal}
            customers={customerOptions}
            tickets={tickets}
            quotes={quotes}
            jobs={jobs}
            quoteCustomerId={quoteCustomerId}
            setQuoteCustomerId={setQuoteCustomerId}
            quoteProjectType={quoteProjectType}
            setQuoteProjectType={setQuoteProjectType}
            quoteOpportunityId={quoteOpportunityId}
            setQuoteOpportunityId={setQuoteOpportunityId}
            quoteLineItems={quoteLineItems}
            onUpdateQuoteLineItem={updateQuoteLineItem}
            onAddQuoteLineItem={addQuoteLineItem}
            onRemoveQuoteLineItem={removeQuoteLineItem}
            onApplyProjectTypeTemplate={applyProjectTypeTemplate}
            jobTitle={jobTitle}
            setJobTitle={setJobTitle}
            jobQuoteId={jobQuoteId}
            setJobQuoteId={setJobQuoteId}
            jobTicketId={jobTicketId}
            setJobTicketId={setJobTicketId}
            onApplyJobTitleFromSelection={applyJobTitleFromSelection}
            projectTypeOptions={projectTypeOptions}
            quoteOpportunityOptions={quoteOpportunityOptions}
            quoteOpportunities={quoteOpportunities}
            quoteOpportunitiesLoading={quoteOpportunitiesLoading}
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
        {proofTicket || proofJob ? (
          <ProofModal
            target={proofTicket ? { kind: "ticket", ...proofTicket } : { kind: "job", ...proofJob }}
            proofFiles={proofTicket
              ? proofFiles.filter((file) => file.ticketId === proofTicket.id)
              : proofFiles.filter((file) => file.jobOrderId === proofJob?.id)}
            onOpenProofReview={openProofReview}
            onClose={() => {
              setProofTicket(null);
              setProofJob(null);
            }}
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
            className="inline-flex h-10 w-10 items-center justify-center rounded-md bg-mint text-white transition hover:bg-blue-700 disabled:cursor-not-allowed disabled:opacity-35"
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

function CrmPanel({
  customers,
  crmLeads,
  crmDetail,
  activeCustomerId,
  crmLoading,
  projectTypeOptions,
  onSelectCustomer,
  onSaveAccount,
  onArchiveAccount,
  onSubmitLead,
  onSubmitContact,
  onSubmitOpportunity,
  onSubmitActivity
}) {
  const account = crmDetail?.account;
  const contacts = crmDetail?.contacts ?? [];
  const opportunities = crmDetail?.opportunities ?? [];
  const activities = crmDetail?.activities ?? [];
  const recentQuotes = crmDetail?.recentQuotes ?? [];
  const recentJobs = crmDetail?.recentJobs ?? [];
  const recentTickets = crmDetail?.recentTickets ?? [];
  const recentFiles = crmDetail?.recentFiles ?? [];
  const metrics = crmDetail?.metrics ?? {};
  const [accountSearch, setAccountSearch] = useState("");
  const [leadSearch, setLeadSearch] = useState("");
  const [contactSearch, setContactSearch] = useState("");
  const [opportunitySearch, setOpportunitySearch] = useState("");
  const [activitySearch, setActivitySearch] = useState("");
  const [accountPage, setAccountPage] = useState(1);
  const [leadPage, setLeadPage] = useState(1);
  const [contactPage, setContactPage] = useState(1);
  const [opportunityPage, setOpportunityPage] = useState(1);
  const [activityPage, setActivityPage] = useState(1);
  const [crmView, setCrmView] = useState("inquiry");
  const [accountForm, setAccountForm] = useState(createBlankAccountForm());
  const [accountEditorMode, setAccountEditorMode] = useState("create");
  const [accountEditorOpen, setAccountEditorOpen] = useState(false);

  useEffect(() => {
    setContactSearch("");
    setOpportunitySearch("");
    setActivitySearch("");
    setContactPage(1);
    setOpportunityPage(1);
    setActivityPage(1);
  }, [activeCustomerId]);

  useEffect(() => {
    if (!accountEditorOpen) {
      return;
    }

    if (accountEditorMode === "edit" && account) {
      setAccountForm({
        companyName: account.companyName ?? "",
        contactName: account.contactName ?? "",
        email: account.email ?? "",
        phone: account.phone ?? "",
        address: account.address ?? "",
        notes: account.notes ?? ""
      });
    } else {
      setAccountForm(createBlankAccountForm());
    }
  }, [account, accountEditorMode, accountEditorOpen]);

  const filteredAccounts = useMemo(
    () => filterItems(customers, accountSearch, [
      (customer) => customer.companyName,
      (customer) => customer.contactName,
      (customer) => customer.email,
      (customer) => customer.phone,
      (customer) => customer.address,
      (customer) => customer.notes
    ]),
    [customers, accountSearch]
  );
  const filteredLeads = useMemo(
    () => filterItems(crmLeads, leadSearch, [
      (lead) => lead.companyName,
      (lead) => lead.contactName,
      (lead) => lead.email,
      (lead) => lead.phone,
      (lead) => lead.source,
      (lead) => lead.status,
      (lead) => lead.notes
    ]),
    [crmLeads, leadSearch]
  );
  const filteredContacts = useMemo(
    () => filterItems(contacts, contactSearch, [
      (contact) => contact.firstName,
      (contact) => contact.lastName,
      (contact) => contact.title,
      (contact) => contact.email,
      (contact) => contact.phone,
      (contact) => contact.notes
    ]),
    [contacts, contactSearch]
  );
  const filteredOpportunities = useMemo(
    () => filterItems(opportunities, opportunitySearch, [
      (opportunity) => opportunity.title,
      (opportunity) => opportunity.stage,
      (opportunity) => opportunity.source,
      (opportunity) => opportunity.notes,
      (opportunity) => opportunity.estimatedValue
    ]),
    [opportunities, opportunitySearch]
  );
  const filteredActivities = useMemo(
    () => filterItems(activities, activitySearch, [
      (activity) => activity.subject,
      (activity) => activity.type,
      (activity) => activity.assignedTo,
      (activity) => activity.status,
      (activity) => activity.opportunityTitle,
      (activity) => activity.notes
    ]),
    [activities, activitySearch]
  );

  const pagedAccounts = paginateItems(filteredAccounts, accountPage);
  const pagedLeads = paginateItems(filteredLeads, leadPage);
  const pagedContacts = paginateItems(filteredContacts, contactPage);
  const pagedOpportunities = paginateItems(filteredOpportunities, opportunityPage);
  const pagedActivities = paginateItems(filteredActivities, activityPage);
  const crmFlowSteps = [
    { id: "inquiry", label: "Inquiry" },
    { id: "account", label: "Account" },
    { id: "opportunity", label: "Opportunity" },
    { id: "follow-up", label: "Follow-Up" },
    { id: "quote-job", label: "Quote / Job" },
    { id: "files-production", label: "Files / Production" }
  ];
  const needsAccount = crmView !== "inquiry";
  const charts = crmDetail?.charts ?? { opportunityStageBreakdown: [], jobStatusBreakdown: [], ticketStatusBreakdown: [] };

  function handleAddAccount() {
    setAccountForm(createBlankAccountForm());
    setAccountEditorMode("create");
    setAccountEditorOpen(true);
  }

  function handleEditAccount() {
    if (!account) {
      return;
    }

    setAccountEditorMode("edit");
    setAccountEditorOpen(true);
  }

  function handleCloseAccountEditor() {
    setAccountEditorOpen(false);
    setAccountForm(createBlankAccountForm());
  }

  function handleAccountFieldChange(event) {
    const { name, value } = event.target;
    setAccountForm((current) => ({ ...current, [name]: value }));
  }

  async function handleAccountSubmit(event) {
    event.preventDefault();
    const payload = {
      companyName: accountForm.companyName.trim(),
      contactName: accountForm.contactName.trim(),
      email: accountForm.email.trim(),
      phone: accountForm.phone.trim(),
      address: accountForm.address.trim(),
      notes: accountForm.notes.trim()
    };
    const saved = await onSaveAccount(payload, accountEditorMode === "edit" ? activeCustomerId : null);
    if (saved?.id) {
      await onSelectCustomer(saved.id);
      setAccountEditorMode("edit");
    }
    setAccountEditorOpen(false);
  }

  async function handleArchiveSelectedAccount() {
    if (!activeCustomerId) {
      return;
    }

    await onArchiveAccount(activeCustomerId);
    setAccountForm(createBlankAccountForm());
    setAccountEditorMode("create");
  }

  return (
    <section className="grid gap-4 rounded-[28px] border border-mint/10 bg-gradient-to-br from-blue-100/60 via-white to-orange-50 p-4 shadow-[0_24px_60px_-40px_rgba(37,99,235,0.45)]">
      <div className="grid gap-2 rounded-2xl border border-mint/10 bg-gradient-to-r from-blue-50 via-white to-orange-50 p-4 lg:grid-cols-6">
        {crmFlowSteps.map((step, index) => (
          <button
            key={step.id}
            className={`flex items-center gap-3 rounded-xl px-3 py-2 text-left shadow-sm transition ${crmView === step.id ? "bg-mint text-white" : "bg-white/85 text-ink hover:bg-white"}`}
            type="button"
            onClick={() => setCrmView(step.id)}
          >
            <span className={`inline-flex h-7 w-7 items-center justify-center rounded-full text-xs font-bold ${crmView === step.id ? "bg-white/20 text-white" : index < 4 ? "bg-mint text-white" : "bg-signal text-white"}`}>{index + 1}</span>
            <span className="text-sm font-semibold">{step.label}</span>
          </button>
        ))}
      </div>

      <div className={`grid gap-4 ${crmView === "inquiry" ? "xl:grid-cols-1" : "xl:grid-cols-[320px_1fr]"}`}>
        {crmView === "inquiry" ? (
          <div className="panel border-signal/15 bg-white/95 p-4">
          <div>
            <h2 className="text-lg font-bold text-ink">New Inquiries</h2>
            <p className="mt-1 text-sm text-steel">Capture inbound sign requests before they turn into active customer accounts and active sales work.</p>
          </div>
          <form className="mt-4 grid gap-3 border-t border-slate-200 pt-4" onSubmit={onSubmitLead}>
            <Input name="companyName" label="Company" required />
            <Input name="contactName" label="Contact" required />
            <div className="grid gap-3 sm:grid-cols-2">
              <Input name="email" label="Email" type="email" />
              <Input name="phone" label="Phone" />
            </div>
            <div className="grid gap-3 sm:grid-cols-2">
              <Input name="source" label="Source" placeholder="Website, walk-in, referral" />
              <Select name="status" label="Status" defaultValue="New">
                {leadStatuses.map((status) => <option key={status}>{status}</option>)}
              </Select>
            </div>
            <Input name="estimatedValue" label="Estimated Value" type="number" min="0" step="0.01" />
            <Textarea name="notes" label="Notes" placeholder="Scope, sign types, site notes, target timeline" />
            <button className="btn w-fit" type="submit"><Plus size={16} /> Add Inquiry</button>
          </form>

          <div className="mt-5">
            <label className="grid gap-1">
              <span className="label">Search Inquiries</span>
              <input className="field" value={leadSearch} onChange={(event) => { setLeadSearch(event.target.value); setLeadPage(1); }} placeholder="Search company, contact, source, status" />
            </label>
          </div>
          <div className="mt-5 divide-y divide-slate-200">
            {pagedLeads.items.map((lead) => (
              <article key={lead.id} className="grid gap-1 py-3">
                <div className="flex items-start justify-between gap-3">
                  <div>
                    <p className="text-sm font-semibold text-ink">{lead.companyName}</p>
                    <p className="text-xs font-medium text-mint">{lead.contactName || "No contact"} / {lead.source || "Unspecified source"}</p>
                  </div>
                  <span className={`rounded-md px-2 py-1 text-xs font-bold ${leadStatusTone(lead.status)}`}>{lead.status}</span>
                </div>
                <p className="text-sm text-steel">{lead.email || lead.phone || "No direct contact details yet."}</p>
                <p className="text-sm text-steel">{lead.estimatedValue ? `Estimated ${formatMoney(lead.estimatedValue)}` : "Value still being qualified."}</p>
              </article>
            ))}
            {crmLeads.length === 0 ? <p className="py-3 text-sm text-steel">No inquiries yet.</p> : null}
            {crmLeads.length > 0 && filteredLeads.length === 0 ? <p className="py-3 text-sm text-steel">No inquiries match this search.</p> : null}
          </div>
          <PaginationControls currentPage={pagedLeads.page} totalItems={filteredLeads.length} onPageChange={setLeadPage} />
          </div>
        ) : (
          <>
            <div className="grid gap-4">
              <div className="panel border-mint/15 bg-white/95 p-4">
                <div className="flex items-center justify-between gap-3">
                  <div>
                    <h2 className="text-lg font-bold text-ink">Accounts</h2>
                    <p className="mt-1 text-sm text-steel">Choose an account to work the selected CRM step.</p>
                  </div>
                  <span className="rounded-md bg-blue-50 px-2 py-1 text-xs font-bold text-mint">{filteredAccounts.length}</span>
                </div>
                <div className="mt-4 grid gap-3">
                  <label className="grid gap-1">
                    <span className="label">Search Accounts</span>
                    <input className="field" value={accountSearch} onChange={(event) => { setAccountSearch(event.target.value); setAccountPage(1); }} placeholder="Search company, contact, email, phone" />
                  </label>
                  {crmView === "account" ? <button className="btn w-fit" type="button" onClick={handleAddAccount}><Plus size={16} /> Add Account</button> : null}
                </div>
                <div className="mt-4 overflow-hidden rounded-xl border border-slate-200">
                  <table className="min-w-full divide-y divide-slate-200 text-left text-sm">
                    <thead className="bg-slate-50">
                      <tr>
                        <th className="px-3 py-2 font-semibold text-ink">Company</th>
                        <th className="px-3 py-2 font-semibold text-ink">Primary Contact</th>
                        <th className="px-3 py-2 font-semibold text-ink">Phone</th>
                      </tr>
                    </thead>
                    <tbody className="divide-y divide-slate-200 bg-white">
                      {pagedAccounts.items.map((customer) => (
                        <tr
                          key={customer.id}
                          className={`cursor-pointer transition ${activeCustomerId === customer.id ? "bg-blue-50" : "hover:bg-slate-50"}`}
                          onClick={() => onSelectCustomer(customer.id)}
                        >
                          <td className="px-3 py-3 font-semibold text-ink">{customer.companyName}</td>
                          <td className="px-3 py-3 text-steel">{customer.contactName || "No contact"}</td>
                          <td className="px-3 py-3 text-steel">{customer.phone || customer.email || "No direct details"}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                  {customers.length === 0 ? <p className="border-t border-slate-200 p-4 text-sm text-steel">Create your first account to start tracking contacts, opportunities, and follow-ups.</p> : null}
                  {customers.length > 0 && filteredAccounts.length === 0 ? <p className="border-t border-slate-200 p-4 text-sm text-steel">No accounts match this search.</p> : null}
                </div>
                <PaginationControls currentPage={pagedAccounts.page} totalItems={filteredAccounts.length} onPageChange={setAccountPage} />
              </div>
            </div>

            <div className="grid gap-4">
              {crmLoading ? <div className="panel border-mint/15 bg-white/95 p-6 text-sm text-steel">Loading account detail...</div> : null}
              {!crmLoading && !account && needsAccount && crmView !== "account" ? <div className="panel border-mint/15 bg-white/95 p-6 text-sm text-steel">Select an account to open the {crmFlowSteps.find((step) => step.id === crmView)?.label ?? "CRM"} workspace.</div> : null}
              {!crmLoading && crmView === "account" ? (
                <>
                  {account ? (
                    <section className="overflow-hidden rounded-[24px] border border-mint/15 bg-white/95 shadow-sm">
                      <div className="bg-gradient-to-r from-blue-50 via-white to-orange-50 p-5">
                        <div className="flex flex-col gap-4 xl:flex-row xl:items-start xl:justify-between">
                          <div>
                            <p className="label">Account Overview</p>
                            <h2 className="mt-1 text-3xl font-bold text-ink">{account.companyName}</h2>
                            <p className="mt-2 max-w-3xl text-sm text-steel">
                              Review the customer snapshot first, then move into opportunities, follow-up, quoting, and production.
                            </p>
                          </div>
                          <div className="flex flex-wrap gap-2">
                            <button className="btn w-fit" type="button" onClick={handleEditAccount}>Edit Account</button>
                            <button className="btn-secondary w-fit border-rose-300 text-rose-700 hover:bg-rose-50" type="button" onClick={handleArchiveSelectedAccount}>
                              Archive Account
                            </button>
                          </div>
                        </div>
                        <div className="mt-5 grid gap-3 md:grid-cols-2 xl:grid-cols-4">
                          <AccountDetailItem label="Primary Contact" value={account.contactName || "Not set"} />
                          <AccountDetailItem label="Email" value={account.email || "Not set"} />
                          <AccountDetailItem label="Phone" value={account.phone || "Not set"} />
                          <AccountDetailItem label="Address" value={account.address || "Not set"} />
                        </div>
                        {account.notes ? (
                          <div className="mt-4 rounded-2xl border border-slate-200 bg-white/80 p-4">
                            <p className="label">Account Notes</p>
                            <p className="mt-2 text-sm text-steel">{account.notes}</p>
                          </div>
                        ) : null}
                      </div>
                    </section>
                  ) : (
                    <section className="panel border-mint/15 bg-white/95 p-6">
                      <p className="label">Account Overview</p>
                      <h2 className="mt-2 text-2xl font-bold text-ink">Select an account</h2>
                      <p className="mt-2 max-w-2xl text-sm text-steel">
                        Use the search table to pick a customer. The right side will switch to a clean report view with counts, pipeline, and current work.
                      </p>
                      <div className="mt-5 grid gap-3 md:grid-cols-3">
                        <EmptyHintCard title="Sales Snapshot" detail="Open opportunities, quote volume, pipeline value, and recent activity." />
                        <EmptyHintCard title="Operations Snapshot" detail="Active jobs, open tickets, files, and production status." />
                        <EmptyHintCard title="Quick Actions" detail="Open edit only when needed, instead of showing a form all the time." />
                      </div>
                    </section>
                  )}

                  {account ? (
                    <section className="grid gap-4 2xl:grid-cols-[minmax(0,1.2fr)_minmax(320px,0.8fr)]">
                      <div className="grid gap-4">
                        <section className="panel border-mint/15 bg-white/95 p-5">
                          <div className="flex items-center justify-between gap-3">
                            <div>
                              <h3 className="text-lg font-bold text-ink">Sales Snapshot</h3>
                              <p className="mt-1 text-sm text-steel">The numbers that matter before you quote or follow up.</p>
                            </div>
                          </div>
                          <div className="mt-4 grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
                            <CrmMetric label="Open Opportunities" value={String(metrics.openOpportunities ?? 0)} />
                            <CrmMetric label="Pipeline Value" value={formatMoney(metrics.pipelineValue ?? 0)} />
                            <CrmMetric label="Quotes" value={String(metrics.totalQuotes ?? 0)} />
                            <CrmMetric label="Last Activity" value={metrics.lastActivityAt ? formatDate(metrics.lastActivityAt) : "No activity"} />
                          </div>
                        </section>

                        <ChartPanel
                          title="Opportunity Pipeline"
                          items={charts.opportunityStageBreakdown}
                          valueFormatter={(item) => `${item.Count} / ${formatMoney(item.Value)}`}
                          tone="mint"
                        />

                        <ListPanel title="Recent Quotes" className="p-5" compact>
                          {recentQuotes.map((quote) => (
                            <Row
                              key={quote.id}
                              title={`${quote.quoteNumber} / ${formatMoney(quote.total)}`}
                              meta={quote.status}
                              detail={`${quote.projectType ? `${quote.projectType} / ` : ""}${quote.opportunityTitle ? `${quote.opportunityTitle} / ` : ""}${quote.validUntil ? `Valid until ${formatDate(quote.validUntil)}` : "No expiry"}`}
                            />
                          ))}
                          {recentQuotes.length === 0 ? <p className="py-3 text-sm text-steel">No quotes for this account yet.</p> : null}
                        </ListPanel>
                      </div>

                      <div className="grid gap-4">
                        <section className="panel border-signal/15 bg-white/95 p-5">
                          <h3 className="text-lg font-bold text-ink">Operations Snapshot</h3>
                          <p className="mt-1 text-sm text-steel">What is active right now for production and customer service.</p>
                          <div className="mt-4 grid gap-3 sm:grid-cols-2">
                            <CrmMetric label="Active Jobs" value={String(metrics.activeJobs ?? 0)} />
                            <CrmMetric label="Open Tickets" value={String(metrics.openTickets ?? 0)} />
                            <CrmMetric label="Open Activities" value={String(metrics.openActivities ?? 0)} />
                            <CrmMetric label="Files" value={String(recentFiles.length ?? 0)} />
                          </div>
                        </section>

                        <ChartPanel
                          title="Production Status"
                          items={[
                            ...charts.jobStatusBreakdown.map((item) => ({ ...item, Label: `Job • ${item.Label}` })),
                            ...charts.ticketStatusBreakdown.map((item) => ({ ...item, Label: `Ticket • ${item.Label}` }))
                          ]}
                          valueFormatter={(item) => `${item.Count}`}
                          tone="signal"
                        />

                        <ListPanel title="Recent Work" className="p-5" compact>
                          {recentJobs.slice(0, 3).map((job) => (
                            <Row key={job.id} title={`${job.jobNumber} / ${job.title}`} meta={job.status} detail={job.dueDate ? `Due ${formatDate(job.dueDate)}` : "No due date"} />
                          ))}
                          {recentTickets.slice(0, 2).map((ticket) => (
                            <Row key={`ticket-${ticket.id}`} title={ticket.title} meta={`${ticket.status} / ${ticket.priority}`} detail={ticket.dueDate ? `Due ${formatDate(ticket.dueDate)}` : "No due date"} />
                          ))}
                          {recentFiles.slice(0, 2).map((file) => (
                            <Row key={`file-${file.id}`} title={file.originalFileName} meta={`${Math.max(1, Math.round(file.sizeBytes / 1024))} KB`} detail={`Uploaded ${formatDateTime(file.uploadedAt)}`} />
                          ))}
                          {recentJobs.length === 0 && recentTickets.length === 0 && recentFiles.length === 0 ? <p className="py-3 text-sm text-steel">No recent work is tied to this account yet.</p> : null}
                        </ListPanel>
                      </div>
                    </section>
                  ) : null}
                </>
              ) : null}

              {!crmLoading && account && crmView === "opportunity" ? (
                <section className="panel border-signal/15 bg-white/95 p-4">
                <div className="flex items-center justify-between gap-3">
                  <h3 className="text-lg font-bold text-ink">Opportunities</h3>
                  <span className="rounded-md bg-blue-50 px-2 py-1 text-xs font-bold text-mint">{filteredOpportunities.length}</span>
                </div>
                <form className="mt-4 grid gap-3 border-t border-slate-200 pt-4" onSubmit={onSubmitOpportunity}>
                  <Input name="title" label="Opportunity" required />
                  <div className="grid gap-3 sm:grid-cols-2">
                    <Select name="projectType" label="Project Type" required defaultValue="">
                      <option value="">Select project type</option>
                      {projectTypeOptions}
                    </Select>
                    <Select name="stage" label="Stage" defaultValue="Lead">
                      {opportunityStages.map((stage) => <option key={stage}>{stage}</option>)}
                    </Select>
                  </div>
                  <Input name="source" label="Source" placeholder="Referral, repeat, web" />
                  <div className="grid gap-3 sm:grid-cols-2">
                    <Input name="estimatedValue" label="Estimated Value" type="number" min="0" step="0.01" defaultValue="0" />
                    <Input name="probability" label="Probability %" type="number" min="0" max="100" defaultValue="50" />
                  </div>
                  <Input name="expectedCloseDate" label="Expected Close" type="date" />
                  <Textarea name="notes" label="Notes" placeholder="Scope, materials, permit notes, decision criteria" />
                  <button className="btn w-fit" type="submit"><Plus size={16} /> Add Opportunity</button>
                </form>
                <div className="mt-5">
                  <label className="grid gap-1">
                    <span className="label">Search Opportunities</span>
                    <input className="field" value={opportunitySearch} onChange={(event) => { setOpportunitySearch(event.target.value); setOpportunityPage(1); }} placeholder="Search title, stage, source, notes" />
                  </label>
                </div>
                <div className="mt-5 divide-y divide-slate-200">
                  {pagedOpportunities.items.map((opportunity) => (
                    <article key={opportunity.id} className="grid gap-1 py-3">
                      <div className="flex items-start justify-between gap-3">
                        <p className="text-sm font-semibold text-ink">{opportunity.title}</p>
                        <span className={`rounded-md px-2 py-1 text-xs font-bold ${opportunityStageTone(opportunity.stage)}`}>{opportunity.stage}</span>
                      </div>
                      <p className="text-xs font-medium text-mint">{opportunity.projectType || "No project type"} / {formatMoney(opportunity.estimatedValue)} / {opportunity.probability}% / {opportunity.source || "No source"}</p>
                      <p className="text-sm text-steel">{opportunity.expectedCloseDate ? `Expected close ${formatDate(opportunity.expectedCloseDate)}` : "Close date not scheduled."}</p>
                      {opportunity.notes ? <p className="text-sm text-steel">{opportunity.notes}</p> : null}
                    </article>
                  ))}
                  {opportunities.length === 0 ? <p className="py-3 text-sm text-steel">No opportunities added yet.</p> : null}
                  {opportunities.length > 0 && filteredOpportunities.length === 0 ? <p className="py-3 text-sm text-steel">No opportunities match this search.</p> : null}
                </div>
                <PaginationControls currentPage={pagedOpportunities.page} totalItems={filteredOpportunities.length} onPageChange={setOpportunityPage} />
                </section>
              ) : null}

              {!crmLoading && account && crmView === "follow-up" ? (
                <section className="panel border-mint/15 bg-white/95 p-4">
                <div className="flex items-center justify-between gap-3">
                  <h3 className="text-lg font-bold text-ink">Activities</h3>
                  <span className="rounded-md bg-blue-50 px-2 py-1 text-xs font-bold text-mint">{filteredActivities.length}</span>
                </div>
                <form className="mt-4 grid gap-3 border-t border-slate-200 pt-4" onSubmit={onSubmitActivity}>
                  <div className="grid gap-3 sm:grid-cols-2">
                    <Select name="type" label="Type" defaultValue="Call">
                      {activityTypes.map((type) => <option key={type}>{type}</option>)}
                    </Select>
                    <Select name="status" label="Status" defaultValue="Open">
                      {activityStatuses.map((status) => <option key={status}>{status}</option>)}
                    </Select>
                  </div>
                  <Input name="subject" label="Subject" required />
                  <div className="grid gap-3 sm:grid-cols-2">
                    <Input name="assignedTo" label="Assigned To" />
                    <Input name="dueAt" label="Due Date" type="date" />
                  </div>
                  <Select name="opportunityId" label="Related Opportunity" defaultValue="">
                    <option value="">General account follow-up</option>
                    {opportunities.map((opportunity) => <option key={opportunity.id} value={opportunity.id}>{opportunity.title}</option>)}
                  </Select>
                  <Textarea name="notes" label="Notes" placeholder="Call outcome, next step, survey checklist, approval blocker" />
                  <button className="btn w-fit" type="submit"><Plus size={16} /> Add Activity</button>
                </form>
                <div className="mt-5">
                  <label className="grid gap-1">
                    <span className="label">Search Activities</span>
                    <input className="field" value={activitySearch} onChange={(event) => { setActivitySearch(event.target.value); setActivityPage(1); }} placeholder="Search subject, type, assigned to, linked opportunity" />
                  </label>
                </div>
                <div className="mt-5 divide-y divide-slate-200">
                  {pagedActivities.items.map((activity) => (
                    <article key={activity.id} className="grid gap-1 py-3">
                      <div className="flex items-start justify-between gap-3">
                        <p className="text-sm font-semibold text-ink">{activity.subject}</p>
                        <span className={`rounded-md px-2 py-1 text-xs font-bold ${activityStatusTone(activity.status)}`}>{activity.status}</span>
                      </div>
                      <p className="text-xs font-medium text-mint">{activity.type} / {activity.assignedTo || "Unassigned"}</p>
                      <p className="text-sm text-steel">{activity.opportunityTitle ? `Linked to ${activity.opportunityTitle}` : "Account-level follow-up"}</p>
                      <p className="text-sm text-steel">{activity.dueAt ? `Due ${formatDate(activity.dueAt)}` : "No due date set."}</p>
                      {activity.notes ? <p className="text-sm text-steel">{activity.notes}</p> : null}
                    </article>
                  ))}
                  {activities.length === 0 ? <p className="py-3 text-sm text-steel">No activities logged yet.</p> : null}
                  {activities.length > 0 && filteredActivities.length === 0 ? <p className="py-3 text-sm text-steel">No activities match this search.</p> : null}
                </div>
                <PaginationControls currentPage={pagedActivities.page} totalItems={filteredActivities.length} onPageChange={setActivityPage} />
                </section>
              ) : null}

              {!crmLoading && account && crmView === "quote-job" ? (
                <section className="grid gap-4 xl:grid-cols-2">
                  <ListPanel title="Recent Quotes">
                {recentQuotes.map((quote) => (
                  <Row
                    key={quote.id}
                    title={`${quote.quoteNumber} / ${formatMoney(quote.total)}`}
                    meta={quote.status}
                    detail={`${quote.projectType ? `${quote.projectType} / ` : ""}${quote.opportunityTitle ? `${quote.opportunityTitle} / ` : ""}${quote.validUntil ? `Valid until ${formatDate(quote.validUntil)}` : "No expiry"}`}
                  />
                ))}
                {recentQuotes.length === 0 ? <p className="py-3 text-sm text-steel">No quotes for this account yet.</p> : null}
                  </ListPanel>
                  <ListPanel title="Recent Jobs">
                {recentJobs.map((job) => (
                  <Row key={job.id} title={`${job.jobNumber} / ${job.title}`} meta={job.status} detail={job.dueDate ? `Due ${formatDate(job.dueDate)}` : "No due date"} />
                ))}
                {recentJobs.length === 0 ? <p className="py-3 text-sm text-steel">No jobs created from this account yet.</p> : null}
                  </ListPanel>
                </section>
              ) : null}

              {!crmLoading && account && crmView === "files-production" ? (
                <section className="grid gap-4 xl:grid-cols-2">
                  <ListPanel title="Recent Tickets">
                {recentTickets.map((ticket) => (
                  <Row key={ticket.id} title={ticket.title} meta={`${ticket.status} / ${ticket.priority}`} detail={ticket.dueDate ? `Due ${formatDate(ticket.dueDate)}` : "No due date"} />
                ))}
                {recentTickets.length === 0 ? <p className="py-3 text-sm text-steel">No tickets tied to this account yet.</p> : null}
                  </ListPanel>
                  <ListPanel title="Recent Files">
                {recentFiles.map((file) => (
                  <Row key={file.id} title={file.originalFileName} meta={`${Math.max(1, Math.round(file.sizeBytes / 1024))} KB`} detail={formatDateTime(file.uploadedAt)} />
                ))}
                {recentFiles.length === 0 ? <p className="py-3 text-sm text-steel">No saved files for this account yet.</p> : null}
                  </ListPanel>
                </section>
              ) : null}
            </div>
          </>
        )}
      </div>
      {accountEditorOpen ? (
        <Modal title={accountEditorMode === "edit" ? "Edit Account" : "Add Account"} onClose={handleCloseAccountEditor}>
          <form className="grid gap-3" onSubmit={handleAccountSubmit}>
            <div className="grid gap-3 xl:grid-cols-2">
              <Input label="Company" name="companyName" required value={accountForm.companyName} onChange={handleAccountFieldChange} />
              <Input label="Primary Contact" name="contactName" value={accountForm.contactName} onChange={handleAccountFieldChange} />
            </div>
            <div className="grid gap-3 xl:grid-cols-2">
              <Input label="Email" name="email" type="email" value={accountForm.email} onChange={handleAccountFieldChange} />
              <Input label="Phone" name="phone" value={accountForm.phone} onChange={handleAccountFieldChange} />
            </div>
            <Input label="Address" name="address" value={accountForm.address} onChange={handleAccountFieldChange} />
            <Textarea label="Notes" name="notes" value={accountForm.notes} onChange={handleAccountFieldChange} placeholder="Brand standards, install notes, billing requirements, reorder notes" />
            <div className="flex flex-wrap gap-2">
              <button className="btn w-fit" type="submit">
                <Plus size={16} /> {accountEditorMode === "edit" ? "Save Changes" : "Create Account"}
              </button>
              <button className="btn-secondary w-fit" type="button" onClick={handleCloseAccountEditor}>
                Cancel
              </button>
            </div>
          </form>
        </Modal>
      ) : null}
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
  const [quoteSearch, setQuoteSearch] = useState("");
  const [quotePage, setQuotePage] = useState(1);
  const [selectedQuote, setSelectedQuote] = useState(null);
  const filteredQuotes = useMemo(
    () => filterItems(quotes, quoteSearch, [
      (quote) => quote.quoteNumber,
      (quote) => quote.status,
      (quote) => quote.projectType,
      (quote) => quote.scope,
      (quote) => quote.customer?.companyName,
      (quote) => quote.opportunity?.title
    ]),
    [quotes, quoteSearch]
  );
  const pagedQuotes = paginateItems(filteredQuotes, quotePage);

  return (
    <section>
      <div className="panel p-5">
        <div className="flex flex-col gap-4 border-b border-stone-200 pb-4 lg:flex-row lg:items-end lg:justify-between">
          <div>
            <h2 className="text-lg font-bold text-ink">Quotes</h2>
            <p className="mt-1 text-sm text-steel">Search quotes, open the full breakdown, and review generated line items.</p>
          </div>
          <label className="grid gap-1 lg:min-w-[360px]">
            <span className="label">Search Quotes</span>
            <input
              className="field"
              value={quoteSearch}
              onChange={(event) => {
                setQuoteSearch(event.target.value);
                setQuotePage(1);
              }}
              placeholder="Search quote, account, project type, opportunity, status"
            />
          </label>
        </div>

        <div className="mt-4 overflow-hidden rounded-xl border border-slate-200">
          <table className="min-w-full divide-y divide-slate-200 text-left text-sm">
            <thead className="bg-slate-50">
              <tr>
                <th className="px-3 py-2 font-semibold text-ink">Quote</th>
                <th className="px-3 py-2 font-semibold text-ink">Account</th>
                <th className="px-3 py-2 font-semibold text-ink">Project Type</th>
                <th className="px-3 py-2 font-semibold text-ink">Status</th>
                <th className="px-3 py-2 font-semibold text-ink">Total</th>
                <th className="px-3 py-2 font-semibold text-ink">Actions</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-200 bg-white">
              {pagedQuotes.items.map((quote) => (
                <tr key={quote.id} className="hover:bg-slate-50">
                  <td className="px-3 py-3">
                    <p className="font-semibold text-ink">{quote.quoteNumber}</p>
                    <p className="text-xs text-steel">{quote.opportunity?.title || quote.scope || "No linked opportunity"}</p>
                  </td>
                  <td className="px-3 py-3 text-steel">{quote.customer?.companyName ?? "No customer"}</td>
                  <td className="px-3 py-3 text-steel">{quote.projectType || "Custom"}</td>
                  <td className="px-3 py-3">
                    <span className="rounded-md bg-blue-50 px-2 py-1 text-xs font-bold text-mint">{quote.status}</span>
                  </td>
                  <td className="px-3 py-3 font-semibold text-ink">{formatMoney(quote.total)}</td>
                  <td className="px-3 py-3">
                    <button className="btn-secondary h-9 px-3" type="button" onClick={() => setSelectedQuote(quote)}>
                      View
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          {quotes.length === 0 ? <p className="border-t border-slate-200 p-4 text-sm text-steel">No quotes created yet.</p> : null}
          {quotes.length > 0 && filteredQuotes.length === 0 ? <p className="border-t border-slate-200 p-4 text-sm text-steel">No quotes match this search.</p> : null}
        </div>
        <PaginationControls currentPage={pagedQuotes.page} totalItems={filteredQuotes.length} onPageChange={setQuotePage} />
      </div>

      {selectedQuote ? <QuoteDetailModal quote={selectedQuote} onClose={() => setSelectedQuote(null)} /> : null}
    </section>
  );
}

function QuoteDetailModal({ quote, onClose }) {
  const lineItems = quote.lineItems ?? [];

  return (
    <Modal title={`Quote / ${quote.quoteNumber}`} onClose={onClose}>
      <div className="grid gap-5">
        <div className="grid gap-3 sm:grid-cols-2">
          <StatusTile label="Account" value={quote.customer?.companyName ?? "No customer"} />
          <StatusTile label="Status" value={quote.status || "Draft"} />
          <StatusTile label="Project Type" value={quote.projectType || "Custom"} />
          <StatusTile label="Opportunity" value={quote.opportunity?.title || "Not linked"} />
          <StatusTile label="Created" value={formatDateTime(quote.createdAt)} />
          <StatusTile label="Valid Until" value={formatDate(quote.validUntil)} />
        </div>

        <div className="rounded-xl border border-slate-200 bg-slate-50 p-4">
          <p className="label">Scope</p>
          <p className="mt-2 text-sm text-steel">{quote.scope || "No scope entered."}</p>
        </div>

        <div className="overflow-hidden rounded-xl border border-slate-200">
          <table className="min-w-full divide-y divide-slate-200 text-left text-sm">
            <thead className="bg-slate-50">
              <tr>
                <th className="px-3 py-2 font-semibold text-ink">Description</th>
                <th className="px-3 py-2 font-semibold text-ink">Qty</th>
                <th className="px-3 py-2 font-semibold text-ink">Unit Price</th>
                <th className="px-3 py-2 font-semibold text-ink">Line Total</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-200 bg-white">
              {lineItems.map((item) => (
                <tr key={item.id}>
                  <td className="px-3 py-3 text-ink">{item.description}</td>
                  <td className="px-3 py-3 text-steel">{item.quantity}</td>
                  <td className="px-3 py-3 text-steel">{formatMoney(item.unitPrice)}</td>
                  <td className="px-3 py-3 font-semibold text-ink">{formatMoney(item.lineTotal)}</td>
                </tr>
              ))}
            </tbody>
          </table>
          {lineItems.length === 0 ? <p className="border-t border-slate-200 p-4 text-sm text-steel">No line items on this quote.</p> : null}
        </div>

        <div className="grid gap-3 sm:grid-cols-3">
          <StatusTile label="Subtotal" value={formatMoney(quote.subtotal)} />
          <StatusTile label="Tax" value={formatMoney(quote.tax)} />
          <StatusTile label="Total" value={formatMoney(quote.total)} />
        </div>
      </div>
    </Modal>
  );
}

function JobsPanel({ jobs, files, tickets, quotes, jobWorkers, proofFiles, onOpenProof, onOpenProofReview, onSaveJob, onDeleteJob }) {
  const [proofFilter, setProofFilter] = useState("all");
  const [jobSearch, setJobSearch] = useState("");
  const [editingJob, setEditingJob] = useState(null);
  const [jobForm, setJobForm] = useState(createBlankJobForm());
  const jobItems = useMemo(
    () => jobs.map((job) => {
      const jobProofs = proofFiles
        .filter((file) => file.jobOrderId === job.id)
        .sort((left, right) => {
          if ((right.version ?? 0) !== (left.version ?? 0)) {
            return (right.version ?? 0) - (left.version ?? 0);
          }

          return new Date(right.uploadedAt).getTime() - new Date(left.uploadedAt).getTime();
        });
      const latestProof = jobProofs[0] ?? null;
      const workflowState = getProofWorkflowState(latestProof);
      const productionFiles = files
        .filter((file) => file.jobOrderId === job.id && isProductionFileCategory(file.category))
        .sort((left, right) => new Date(right.uploadedAt).getTime() - new Date(left.uploadedAt).getTime());

      return {
        job,
        jobProofs,
        latestProof,
        workflowState,
        productionFiles
      };
    }),
    [files, jobs, proofFiles]
  );
  const proofFilteredJobItems = jobItems.filter(({ latestProof, workflowState }) => {
    if (proofFilter === "all") {
      return true;
    }

    if (proofFilter === "no-proof") {
      return !latestProof;
    }

    if (proofFilter === "approved") {
      return workflowState.label === "Approved for Production";
    }

    if (proofFilter === "revision") {
      return workflowState.label === "Revision Requested";
    }

    if (proofFilter === "sent") {
      return workflowState.label === "Proof Sent";
    }

    return true;
  });
  const filteredJobItems = useMemo(
    () => filterItems(proofFilteredJobItems, jobSearch, [
      ({ job }) => job.jobNumber,
      ({ job }) => job.title,
      ({ job }) => normalizeJobStatus(job.status),
      ({ job }) => job.assignedTo,
      ({ job }) => job.quote?.customer?.companyName,
      ({ job }) => job.quote?.projectType,
      ({ job }) => job.quote?.opportunity?.title,
      ({ job }) => job.ticket?.title
    ]),
    [proofFilteredJobItems, jobSearch]
  );

  function handleEditJob(job) {
    setEditingJob(job);
    setJobForm(createBlankJobForm(job));
  }

  function handleCloseEditor() {
    setEditingJob(null);
    setJobForm(createBlankJobForm());
  }

  function handleJobFieldChange(event) {
    const { checked, name, type, value } = event.target;
    setJobForm((current) => ({ ...current, [name]: type === "checkbox" ? checked : value }));
  }

  function applyJobEditorTitle(nextQuoteId, nextTicketId) {
    const selectedQuote = quotes.find((quote) => String(quote.id) === String(nextQuoteId));
    const selectedTicket = tickets.find((ticket) => String(ticket.id) === String(nextTicketId));
    setJobForm((current) => ({
      ...current,
      quoteId: nextQuoteId,
      ticketId: nextTicketId,
      title: buildJobTitleFromSelection({ quote: selectedQuote, ticket: selectedTicket }) || current.title
    }));
  }

  async function handleSubmitJob(event) {
    event.preventDefault();
    if (!editingJob) {
      return;
    }

    await onSaveJob({
      title: jobForm.title,
      status: jobForm.status,
      assignedTo: jobForm.assignedTo,
      productionNotes: jobForm.productionNotes,
      dxfReady: jobForm.dxfReady,
      bendFileReady: jobForm.bendFileReady,
      backsReady: jobForm.backsReady,
      facesReady: jobForm.facesReady,
      ledLayoutReady: jobForm.ledLayoutReady,
      dueDate: jobForm.dueDate || null,
      ticketId: numberOrNull(jobForm.ticketId),
      quoteId: numberOrNull(jobForm.quoteId)
    }, editingJob.id);
    handleCloseEditor();
  }

  async function handleDeleteJob(job) {
    if (!window.confirm(`Delete job order ${job.jobNumber}?`)) {
      return;
    }

    await onDeleteJob(job.id);
    if (editingJob?.id === job.id) {
      handleCloseEditor();
    }
  }

  return (
    <section>
      <div className="panel p-5">
        <div className="flex flex-col gap-4 border-b border-stone-200 pb-4 xl:flex-row xl:items-end xl:justify-between">
          <div>
            <h2 className="text-lg font-bold text-ink">Job Orders</h2>
            <p className="mt-1 text-sm text-steel">Manage production and keep proofing visible before work moves forward.</p>
          </div>
          <div className="flex flex-col gap-3 xl:items-end">
            <label className="grid gap-1 xl:min-w-[340px]">
              <span className="label">Search Jobs</span>
              <input className="field" value={jobSearch} onChange={(event) => setJobSearch(event.target.value)} placeholder="Search job, customer, project type, opportunity, worker" />
            </label>
            <div className="flex flex-wrap gap-2">
              {[
                { id: "all", label: "All Jobs" },
                { id: "approved", label: "Approved for Production" },
                { id: "revision", label: "Revision Requested" },
                { id: "sent", label: "Proof Sent" },
                { id: "no-proof", label: "No Proof" }
              ].map((filter) => (
                <button
                  key={filter.id}
                  className={`btn-secondary h-9 px-3 ${proofFilter === filter.id ? "border-mint bg-mint text-white hover:border-mint hover:bg-blue-700 hover:text-white" : ""}`}
                  type="button"
                  onClick={() => setProofFilter(filter.id)}
                >
                  {filter.label}
                </button>
              ))}
            </div>
          </div>
        </div>

        <div className="mt-4 overflow-x-auto">
          <table className="min-w-full divide-y divide-slate-200 text-left text-sm">
            <thead className="bg-slate-50">
              <tr>
                <th className="px-4 py-3 font-semibold text-ink">Job</th>
                <th className="px-4 py-3 font-semibold text-ink">Customer / Project</th>
                <th className="px-4 py-3 font-semibold text-ink">Status</th>
                <th className="px-4 py-3 font-semibold text-ink">Proofing</th>
                <th className="px-4 py-3 font-semibold text-ink">Assigned</th>
                <th className="px-4 py-3 font-semibold text-ink">Due</th>
                <th className="px-4 py-3 font-semibold text-ink">Fabrication</th>
                <th className="px-4 py-3 font-semibold text-ink">Actions</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-200 bg-white">
              {filteredJobItems.map(({ job, latestProof, workflowState, productionFiles }) => {
                const checklist = [
                  { label: "DXF", done: job.dxfReady },
                  { label: "Bend File", done: job.bendFileReady },
                  { label: "Backs", done: job.backsReady },
                  { label: "Faces", done: job.facesReady },
                  { label: "LED Layout", done: job.ledLayoutReady }
                ];
                const checklistDone = checklist.filter((item) => item.done).length;

                return (
                <tr key={job.id} className={workflowState.label === "Approved for Production" ? "bg-emerald-50/40" : ""}>
                  <td className="px-4 py-4 align-top">
                    <div className="grid gap-1">
                      <p className="font-semibold text-ink">{job.jobNumber}</p>
                      <p className="text-sm text-steel">{job.title}</p>
                    </div>
                  </td>
                  <td className="px-4 py-4 align-top">
                    <div className="grid gap-1 text-sm">
                      <p className="font-medium text-ink">{job.quote?.customer?.companyName || "No customer"}</p>
                      <p className="text-steel">{job.quote?.opportunity?.title || job.ticket?.title || "No linked opportunity or ticket"}</p>
                      <p className="text-xs text-steel">{job.quote?.projectType || "No project type"}</p>
                    </div>
                  </td>
                  <td className="px-4 py-4 align-top">
                    <div className="grid gap-2">
                      <span className={`w-fit rounded-md px-2 py-1 text-xs font-bold ${jobStatusTone(normalizeJobStatus(job.status))}`}>{normalizeJobStatus(job.status)}</span>
                      {workflowState.label === "Approved for Production" ? (
                        <span className="w-fit rounded-full bg-emerald-100 px-3 py-1 text-xs font-bold text-emerald-800">Approved for Production</span>
                      ) : null}
                    </div>
                  </td>
                  <td className="px-4 py-4 align-top">
                    <div className="grid gap-1 text-sm">
                      <span className={`w-fit rounded-md px-2 py-1 text-xs font-bold ${workflowState.tone}`}>{workflowState.label}</span>
                      <p className="text-steel">{latestProof ? `v${String(latestProof.version).padStart(2, "0")} / ${formatDateTime(latestProof.uploadedAt)}` : "No proof uploaded"}</p>
                      <p className="text-xs text-steel">{latestProof?.originalFileName || "Upload the first proof from this row."}</p>
                    </div>
                  </td>
                  <td className="px-4 py-4 align-top text-sm text-steel">
                    {job.assignedTo || "Unassigned"}
                  </td>
                  <td className="px-4 py-4 align-top text-sm text-steel">
                    {job.dueDate ? formatDate(job.dueDate) : "No due date"}
                  </td>
                  <td className="px-4 py-4 align-top">
                    <div className="grid gap-3 text-sm text-steel">
                      <div className="grid gap-2">
                        <div className="flex flex-wrap items-center gap-2">
                          <span className="text-xs font-semibold uppercase tracking-[0.18em] text-steel">Checklist</span>
                          <span className="rounded-full bg-slate-100 px-2 py-1 text-xs font-bold text-steel">{checklistDone}/{checklist.length} ready</span>
                        </div>
                        <div className="flex flex-wrap gap-2">
                          {checklist.map((item) => (
                            <span key={item.label} className={`rounded-full px-2 py-1 text-xs font-bold ${item.done ? "bg-emerald-100 text-emerald-800" : "bg-stone-100 text-stone-600"}`}>
                              {item.label}
                            </span>
                          ))}
                        </div>
                      </div>
                      <div className="grid gap-1">
                        <span className="text-xs font-semibold uppercase tracking-[0.18em] text-steel">Production Files</span>
                        {productionFiles.length > 0 ? productionFiles.slice(0, 3).map((file) => (
                          <p key={file.id} className="text-xs">{file.category} / {file.originalFileName}</p>
                        )) : <p className="text-xs">No production files uploaded yet.</p>}
                      </div>
                      <div className="grid gap-1">
                        <span className="text-xs font-semibold uppercase tracking-[0.18em] text-steel">Notes</span>
                        <p>{job.productionNotes || latestProof?.notes || "No production notes yet."}</p>
                        {latestProof?.customerComment ? <p className="text-xs text-steel">Customer: {latestProof.customerComment}</p> : null}
                      </div>
                    </div>
                  </td>
                  <td className="px-4 py-4 align-top">
                    <div className="flex flex-col gap-2">
                      <button className="btn-secondary h-9 px-3" type="button" onClick={() => handleEditJob(job)}>
                        Edit
                      </button>
                      <button className="btn-secondary h-9 px-3" type="button" onClick={() => onOpenProof(job)}>
                        {latestProof ? "Manage Proofs" : "Upload Proof"}
                      </button>
                      {latestProof ? (
                        <button className="btn h-9 px-3" type="button" onClick={() => onOpenProofReview(latestProof)}>
                          {latestProof.reviewToken ? "Open Review" : "Send Review Link"}
                        </button>
                      ) : null}
                      <button className="btn-secondary h-9 border-rose-300 px-3 text-rose-700 hover:bg-rose-50" type="button" onClick={() => handleDeleteJob(job)}>
                        Delete
                      </button>
                    </div>
                  </td>
                </tr>
              )})}
            </tbody>
          </table>
          {jobs.length === 0 ? <p className="rounded-b-2xl border border-t-0 border-dashed border-slate-300 bg-slate-50 p-4 text-sm text-steel">No job orders yet.</p> : null}
          {jobs.length > 0 && filteredJobItems.length === 0 ? <p className="rounded-b-2xl border border-t-0 border-dashed border-slate-300 bg-slate-50 p-4 text-sm text-steel">No jobs match this search or proof filter.</p> : null}
        </div>
      </div>
      {editingJob ? (
        <Modal title={`Edit Job / ${editingJob.jobNumber}`} onClose={handleCloseEditor}>
          <form className="grid gap-3" onSubmit={handleSubmitJob}>
            <Input name="title" label="Job Title" required value={jobForm.title} onChange={handleJobFieldChange} />
            <Select
              name="ticketId"
              label="Ticket"
              value={jobForm.ticketId}
              onChange={(event) => applyJobEditorTitle(jobForm.quoteId, event.target.value)}
            >
              <option value="">None</option>
              {tickets.map((ticket) => <option key={ticket.id} value={ticket.id}>{ticket.title}</option>)}
            </Select>
            <Select
              name="quoteId"
              label="Quote"
              value={jobForm.quoteId}
              onChange={(event) => applyJobEditorTitle(event.target.value, jobForm.ticketId)}
            >
              <option value="">None</option>
              {quotes.map((quote) => <option key={quote.id} value={quote.id}>{quote.quoteNumber}</option>)}
            </Select>
            <Select name="assignedTo" label="Assigned To" value={jobForm.assignedTo} onChange={handleJobFieldChange}>
              <option value="">Unassigned</option>
              {jobWorkers.map((worker) => <option key={worker.id} value={worker.displayName}>{worker.displayName}</option>)}
            </Select>
            <Input name="dueDate" label="Due Date" type="date" value={jobForm.dueDate} onChange={handleJobFieldChange} />
            <Select name="status" label="Status" value={jobForm.status} onChange={handleJobFieldChange}>
              {jobStatuses.map((status) => <option key={status}>{status}</option>)}
            </Select>
            <Textarea name="productionNotes" label="Production Notes" value={jobForm.productionNotes} onChange={handleJobFieldChange} />
            <div className="grid gap-2 rounded-2xl border border-stone-200 bg-stone-50 p-4">
              <p className="label">Channel Letter Fabrication Checklist</p>
              <div className="grid gap-2 sm:grid-cols-2">
                <CheckboxField name="dxfReady" label="DXF Ready" checked={jobForm.dxfReady} onChange={handleJobFieldChange} />
                <CheckboxField name="bendFileReady" label="Bend File Ready" checked={jobForm.bendFileReady} onChange={handleJobFieldChange} />
                <CheckboxField name="backsReady" label="Backs Ready" checked={jobForm.backsReady} onChange={handleJobFieldChange} />
                <CheckboxField name="facesReady" label="Faces Ready" checked={jobForm.facesReady} onChange={handleJobFieldChange} />
                <CheckboxField name="ledLayoutReady" label="LED Layout Ready" checked={jobForm.ledLayoutReady} onChange={handleJobFieldChange} />
              </div>
            </div>
            <div className="flex flex-wrap gap-2">
              <button className="btn w-fit" type="submit">
                <Plus size={16} /> Save Changes
              </button>
              <button className="btn-secondary w-fit" type="button" onClick={handleCloseEditor}>
                Cancel
              </button>
            </div>
          </form>
        </Modal>
      ) : null}
    </section>
  );
}

function FilesPanel({ files }) {
  return (
    <section>
      <ListPanel title="Saved Files">
        {files.map((file) => (
          <Row
            key={file.id}
            title={file.originalFileName}
            meta={`${file.category || "General"} / ${file.contentType || "file"} / ${Math.round(file.sizeBytes / 1024)} KB`}
            detail={`${new Date(file.uploadedAt).toLocaleString()}${file.notes ? ` / ${file.notes}` : ""}`}
          />
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
  quoteCustomerId,
  setQuoteCustomerId,
  quoteProjectType,
  setQuoteProjectType,
  quoteOpportunityId,
  setQuoteOpportunityId,
  quoteLineItems,
  onUpdateQuoteLineItem,
  onAddQuoteLineItem,
  onRemoveQuoteLineItem,
  onApplyProjectTypeTemplate,
  jobTitle,
  setJobTitle,
  jobQuoteId,
  setJobQuoteId,
  jobTicketId,
  setJobTicketId,
  onApplyJobTitleFromSelection,
  projectTypeOptions,
  quoteOpportunityOptions,
  quoteOpportunities,
  quoteOpportunitiesLoading,
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
    crm: "Add Account",
    inventory: "Add Inventory",
    quotes: "Generate Quote",
    jobs: "Generate Job Order",
    files: "Add File From System"
  }[activeModal];
  const selectedQuoteOpportunity = quoteOpportunities.find((opportunity) => String(opportunity.id) === quoteOpportunityId);

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
          <Select name="assignedTo" label="Assigned To" defaultValue="">
            <option value="">Unassigned</option>
            {jobWorkers.map((worker) => <option key={worker.id} value={worker.displayName}>{worker.displayName}</option>)}
          </Select>
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
          <Select
            name="customerId"
            label="Customer"
            required
            value={quoteCustomerId}
            onChange={(event) => {
              setQuoteCustomerId(event.target.value);
              setQuoteProjectType("");
              setQuoteOpportunityId("");
              onApplyProjectTypeTemplate("");
            }}
          >
            <option value="">Select account</option>
            {customers}
          </Select>
          <Select
            name="opportunityId"
            label="Related Opportunity"
            value={quoteOpportunityId}
            onChange={(event) => setQuoteOpportunityId(event.target.value)}
            disabled={!quoteCustomerId || quoteOpportunitiesLoading}
          >
            <option value="">
              {!quoteCustomerId
                ? "Select account first"
                : quoteOpportunitiesLoading
                  ? "Loading opportunities..."
                  : quoteOpportunities.length > 0
                    ? "Link to an opportunity"
                    : "No opportunities for this account yet"}
            </option>
            {quoteOpportunityOptions}
          </Select>
          <Select
            name="projectType"
            label="Project Type"
            value={quoteProjectType}
            onChange={(event) => onApplyProjectTypeTemplate(event.target.value)}
            disabled={Boolean(selectedQuoteOpportunity?.projectType)}
            required
          >
            <option value="">Select project type</option>
            {projectTypeOptions}
          </Select>
          <p className="text-sm text-steel">
            {selectedQuoteOpportunity?.projectType
              ? "Project type is inherited from the selected opportunity."
              : "Pick a project type to auto-generate editable line items."}
          </p>
          <p className="text-sm text-steel">Linking a quote to an opportunity keeps the sales pipeline and follow-up automation tied to the right job.</p>
          <Textarea name="scope" label="Scope" />
          <div className="grid gap-3 rounded-2xl border border-slate-200 bg-slate-50 p-4">
            <div className="flex items-center justify-between gap-3">
              <div>
                <p className="label">Line Items</p>
                <p className="mt-1 text-sm text-steel">Generated from the project type, but still fully editable.</p>
              </div>
              <button className="btn-secondary h-9 px-3" type="button" onClick={onAddQuoteLineItem}>
                <Plus size={16} /> Add Line
              </button>
            </div>
            {quoteLineItems.map((item, index) => (
              <div key={`${index}-${item.description}`} className="grid gap-3 rounded-2xl border border-slate-200 bg-white p-3">
                <div className="grid gap-3 xl:grid-cols-[minmax(0,1fr)_110px_140px_auto]">
                  <Input
                    label={`Description ${index + 1}`}
                    value={item.description}
                    onChange={(event) => onUpdateQuoteLineItem(index, "description", event.target.value)}
                  />
                  <Input
                    label="Qty"
                    type="number"
                    min="1"
                    value={item.quantity}
                    onChange={(event) => onUpdateQuoteLineItem(index, "quantity", event.target.value)}
                  />
                  <Input
                    label="Unit Price"
                    type="number"
                    step="0.01"
                    min="0"
                    value={item.unitPrice}
                    onChange={(event) => onUpdateQuoteLineItem(index, "unitPrice", event.target.value)}
                  />
                  <div className="flex items-end">
                    <button className="btn-secondary h-10 px-3" type="button" onClick={() => onRemoveQuoteLineItem(index)}>
                      Remove
                    </button>
                  </div>
                </div>
              </div>
            ))}
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
          <Input
            name="title"
            label="Job Title"
            required
            value={jobTitle}
            onChange={(event) => setJobTitle(event.target.value)}
            placeholder="Auto-filled from quote or ticket"
          />
          <Select
            name="ticketId"
            label="Ticket"
            value={jobTicketId}
            onChange={(event) => {
              setJobTicketId(event.target.value);
              onApplyJobTitleFromSelection({ nextTicketId: event.target.value });
            }}
          >
            <option value="">None</option>
            {tickets.map((ticket) => <option key={ticket.id} value={ticket.id}>{ticket.title}</option>)}
          </Select>
          <Select
            name="quoteId"
            label="Quote"
            value={jobQuoteId}
            onChange={(event) => {
              setJobQuoteId(event.target.value);
              onApplyJobTitleFromSelection({ nextQuoteId: event.target.value });
            }}
          >
            <option value="">None</option>
            {quotes.map((quote) => <option key={quote.id} value={quote.id}>{quote.quoteNumber}</option>)}
          </Select>
          <p className="text-sm text-steel">Selecting a quote auto-fills the title from account, opportunity, and project type. You can still edit it.</p>
          <Input name="assignedTo" label="Assigned To" />
          <Input name="dueDate" label="Due Date" type="date" />
          <Select name="status" label="Status" defaultValue="Artwork">
            {jobStatuses.map((status) => <option key={status}>{status}</option>)}
          </Select>
          <Textarea name="productionNotes" label="Production Notes" />
          <div className="grid gap-2 rounded-2xl border border-stone-200 bg-stone-50 p-4">
            <p className="label">Channel Letter Fabrication Checklist</p>
            <div className="grid gap-2 sm:grid-cols-2">
              <CheckboxField name="dxfReady" label="DXF Ready" />
              <CheckboxField name="bendFileReady" label="Bend File Ready" />
              <CheckboxField name="backsReady" label="Backs Ready" />
              <CheckboxField name="facesReady" label="Faces Ready" />
              <CheckboxField name="ledLayoutReady" label="LED Layout Ready" />
            </div>
          </div>
        </FormPanel>
      ) : null}

      {activeModal === "files" ? (
        <FormPanel title={title} onSubmit={onSubmitFile} multipart>
          <input className="field" name="file" type="file" required />
          <Select name="category" label="File Category" defaultValue="General">
            {fileCategories.map((category) => <option key={category}>{category}</option>)}
          </Select>
          <Select name="customerId" label="Customer"><option value="">None</option>{customers}</Select>
          <Select name="ticketId" label="Ticket"><option value="">None</option>{tickets.map((ticket) => <option key={ticket.id} value={ticket.id}>{ticket.title}</option>)}</Select>
          <Select name="jobOrderId" label="Job Order"><option value="">None</option>{jobs.map((job) => <option key={job.id} value={job.id}>{job.jobNumber}</option>)}</Select>
          <Textarea name="notes" label="File Notes" />
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

function ProofModal({ target, proofFiles, onClose, onSubmitProof, onOpenProofReview }) {
  const webUrl = process.env.NEXT_PUBLIC_WEB_URL ?? "http://localhost:3000";
  const customerId = target.kind === "ticket"
    ? (target.customerId ?? target.customer?.id ?? "")
    : (target.quote?.customerId ?? target.ticket?.customerId ?? "");
  const orderedProofs = [...proofFiles].sort((left, right) => {
    if ((right.version ?? 0) !== (left.version ?? 0)) {
      return (right.version ?? 0) - (left.version ?? 0);
    }

    return new Date(right.uploadedAt).getTime() - new Date(left.uploadedAt).getTime();
  });
  const timeline = buildProofWorkflowTimeline(orderedProofs);

  return (
    <Modal title={`Proofs / ${target.title}`} onClose={onClose}>
      <form className="flex flex-col gap-3" onSubmit={onSubmitProof} encType="multipart/form-data">
        {target.kind === "ticket" ? <input name="ticketId" type="hidden" value={target.id} /> : null}
        {target.kind === "job" ? <input name="jobOrderId" type="hidden" value={target.id} /> : null}
        <input name="customerId" type="hidden" value={customerId} />
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
        <h3 className="text-sm font-bold text-ink">Revision Loop</h3>
        <div className="mt-2 grid gap-3">
          {timeline.map((entry, index) => (
            <article key={`${entry.fileId}-${entry.label}-${index}`} className="rounded-2xl border border-slate-200 bg-slate-50 p-4">
              <div className="flex items-start justify-between gap-3">
                <div>
                  <p className="text-sm font-semibold text-ink">{entry.label}</p>
                  <p className="text-xs text-steel">{entry.detail}</p>
                </div>
                <span className={`shrink-0 rounded-md px-2 py-1 text-xs font-bold ${entry.tone}`}>v{String(entry.version).padStart(2, "0")}</span>
              </div>
            </article>
          ))}
          {timeline.length === 0 ? <p className="text-sm text-steel">No proof workflow yet.</p> : null}
        </div>
      </div>

      <div className="mt-5 border-t border-stone-200 pt-4">
        <h3 className="text-sm font-bold text-ink">Proof History</h3>
        <div className="mt-2 divide-y divide-stone-200">
          {orderedProofs.map((file) => {
            const workflowState = getProofWorkflowState(file);
            return (
            <article key={file.id} className="grid gap-1 py-3">
              <div className="flex items-start justify-between gap-3">
                <div>
                  <p className="text-sm font-semibold text-ink">v{String(file.version).padStart(2, "0")} / {file.originalFileName}</p>
                  <p className="text-xs font-medium text-mint">{workflowState.label} / {file.designer || "Unknown designer"}</p>
                </div>
                <div className="flex items-center gap-2">
                  <span className={`shrink-0 rounded-md px-2 py-1 text-xs font-semibold ${workflowState.tone}`}>{workflowState.label}</span>
                  <span className="shrink-0 rounded-md bg-stone-100 px-2 py-1 text-xs font-semibold text-steel">{Math.round(file.sizeBytes / 1024)} KB</span>
                </div>
              </div>
              <p className="text-xs text-steel">{file.relativePath}</p>
              {file.reviewToken ? (
                <a className="text-sm font-semibold text-mint hover:underline" href={`${webUrl}/proofs/review/${file.reviewToken}`} target="_blank">
                  Open customer review link
                </a>
              ) : null}
              {!file.reviewToken ? (
                <button className="btn-secondary h-9 w-fit px-3" type="button" onClick={() => onOpenProofReview(file)}>
                  Send Review Link
                </button>
              ) : null}
              {file.sentTo ? <p className="text-sm text-steel">Sent to {file.sentTo}</p> : null}
              {file.approvedBy ? <p className="text-sm text-steel">Approved by {file.approvedBy}</p> : null}
              {file.customerRespondedAt ? <p className="text-sm text-steel">Customer responded {formatDateTime(file.customerRespondedAt)}</p> : null}
              {file.customerComment ? <p className="text-sm text-steel">Customer comment: {file.customerComment}</p> : null}
              {file.notes ? <p className="text-sm text-steel">{file.notes}</p> : null}
            </article>
          );
          })}
          {orderedProofs.length === 0 ? <p className="py-3 text-sm text-steel">No proofs uploaded for this {target.kind} yet.</p> : null}
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

function ListPanel({ title, children, className = "", compact = false }) {
  return (
    <div className={`panel ${compact ? "" : "min-h-[360px]"} p-4 ${className}`.trim()}>
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

function CrmMetric({ label, value }) {
  return (
    <div className="rounded-2xl border border-slate-200 bg-white p-3 shadow-sm">
      <p className="label">{label}</p>
      <p className="mt-2 text-xl font-bold text-ink">{value}</p>
    </div>
  );
}

function ChartPanel({ title, items, valueFormatter, tone = "mint" }) {
  const maxCount = Math.max(...items.map((item) => item.Count), 1);
  const barClass = tone === "signal" ? "bg-signal" : "bg-mint";

  return (
    <section className="panel border-mint/15 bg-white/95 p-5">
      <h3 className="text-lg font-bold text-ink">{title}</h3>
      <div className="mt-4 grid gap-3">
        {items.map((item) => (
          <div key={item.Label} className="grid gap-2">
            <div className="flex items-center justify-between gap-3">
              <p className="text-sm font-semibold text-ink">{item.Label}</p>
              <p className="text-xs font-medium text-steel">{valueFormatter(item)}</p>
            </div>
            <div className="h-2 overflow-hidden rounded-full bg-slate-100">
              <div className={`${barClass} h-full rounded-full`} style={{ width: `${Math.max(8, (item.Count / maxCount) * 100)}%` }} />
            </div>
          </div>
        ))}
        {items.length === 0 ? <p className="text-sm text-steel">No data available yet.</p> : null}
      </div>
    </section>
  );
}

function AccountDetailItem({ label, value }) {
  return (
    <div className="rounded-2xl border border-slate-200 bg-white/90 p-4 shadow-sm">
      <p className="label">{label}</p>
      <p className="mt-2 text-sm font-medium text-ink">{value}</p>
    </div>
  );
}

function EmptyHintCard({ title, detail }) {
  return (
    <div className="rounded-2xl border border-slate-200 bg-slate-50 p-4">
      <p className="text-sm font-semibold text-ink">{title}</p>
      <p className="mt-2 text-sm text-steel">{detail}</p>
    </div>
  );
}

function PaginationControls({ currentPage, totalItems, pageSize = CRM_PAGE_SIZE, onPageChange }) {
  const totalPages = Math.max(1, Math.ceil(totalItems / pageSize));
  if (totalItems <= pageSize) {
    return null;
  }

  return (
    <div className="mt-4 flex items-center justify-between gap-3 border-t border-slate-200 pt-3 text-sm text-steel">
      <span>Page {currentPage} of {totalPages}</span>
      <div className="flex items-center gap-2">
        <button className="btn-secondary h-9 px-3" type="button" disabled={currentPage <= 1} onClick={() => onPageChange(currentPage - 1)}>
          Previous
        </button>
        <button className="btn-secondary h-9 px-3" type="button" disabled={currentPage >= totalPages} onClick={() => onPageChange(currentPage + 1)}>
          Next
        </button>
      </div>
    </div>
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

function CheckboxField({ label, ...inputProps }) {
  return (
    <label className="flex items-center gap-2 rounded-xl border border-stone-200 bg-white px-3 py-2 text-sm font-medium text-ink">
      <input type="checkbox" {...inputProps} />
      <span>{label}</span>
    </label>
  );
}

function numberOrNull(value) {
  const parsed = Number(value);
  return Number.isFinite(parsed) && parsed > 0 ? parsed : null;
}

function optionalNumber(value) {
  const parsed = Number(value);
  return Number.isFinite(parsed) && parsed > 0 ? parsed : null;
}

function filterItems(items, search, fields) {
  const query = String(search || "").trim().toLowerCase();
  if (!query) {
    return items;
  }

  return items.filter((item) =>
    fields.some((field) => String(field(item) ?? "").toLowerCase().includes(query))
  );
}

function paginateItems(items, page, pageSize = CRM_PAGE_SIZE) {
  const totalPages = Math.max(1, Math.ceil(items.length / pageSize));
  const safePage = Math.min(Math.max(1, page), totalPages);
  const startIndex = (safePage - 1) * pageSize;
  return {
    page: safePage,
    totalPages,
    items: items.slice(startIndex, startIndex + pageSize)
  };
}

function getProofWorkflowState(proof) {
  if (!proof) {
    return { label: "No proof yet", tone: "bg-stone-100 text-stone-700" };
  }

  if (proof.status === "Approved" || proof.customerDecision === "Approved") {
    return { label: "Approved for Production", tone: "bg-emerald-50 text-emerald-800" };
  }

  if (proof.status === "Revision Needed" || proof.customerDecision === "Revision Needed") {
    return { label: "Revision Requested", tone: "bg-amber-50 text-amber-900" };
  }

  if (proof.sentAt || proof.status === "Sent") {
    return { label: "Proof Sent", tone: "bg-blue-50 text-mint" };
  }

  if (proof.status === "Draft") {
    return { label: "In Design", tone: "bg-stone-100 text-stone-700" };
  }

  return { label: proof.status || "Proofing", tone: "bg-stone-100 text-stone-700" };
}

function buildProofWorkflowTimeline(proofs) {
  return proofs.map((proof) => {
    const workflowState = getProofWorkflowState(proof);
    const detailParts = [proof.originalFileName];

    if (proof.customerDecision === "Approved" && proof.customerRespondedAt) {
      detailParts.push(`Approved ${formatDateTime(proof.customerRespondedAt)}`);
    } else if (proof.customerDecision === "Revision Needed" && proof.customerRespondedAt) {
      detailParts.push(`Revision requested ${formatDateTime(proof.customerRespondedAt)}`);
    } else if (proof.sentAt) {
      detailParts.push(`Sent ${formatDate(proof.sentAt)}`);
    } else {
      detailParts.push(`Uploaded ${formatDateTime(proof.uploadedAt)}`);
    }

    if (proof.customerComment) {
      detailParts.push(proof.customerComment);
    } else if (proof.notes) {
      detailParts.push(proof.notes);
    }

    return {
      fileId: proof.id,
      version: proof.version ?? 1,
      label: workflowState.label,
      tone: workflowState.tone,
      detail: detailParts.filter(Boolean).join(" / ")
    };
  });
}

function formatMoney(value) {
  return new Intl.NumberFormat("en-US", { style: "currency", currency: "USD", maximumFractionDigits: 0 }).format(Number(value || 0));
}

function formatDate(value) {
  return value ? new Date(value).toLocaleDateString() : "No date";
}

function formatDateTime(value) {
  return value ? new Date(value).toLocaleString() : "No date";
}

function leadStatusTone(status) {
  if (status === "Won") {
    return "bg-blue-50 text-mint";
  }

  if (status === "Lost") {
    return "bg-rose-50 text-rose-800";
  }

  if (status === "Quoted") {
    return "bg-signal/10 text-signal";
  }

  return "bg-slate-100 text-slate-700";
}

function opportunityStageTone(stage) {
  if (stage === "Won") {
    return "bg-blue-50 text-mint";
  }

  if (stage === "Lost") {
    return "bg-rose-50 text-rose-800";
  }

  if (stage === "Survey" || stage === "Quoted" || stage === "Negotiation") {
    return "bg-signal/10 text-signal";
  }

  return "bg-slate-100 text-slate-700";
}

function activityStatusTone(status) {
  return status === "Completed" ? "bg-slate-100 text-slate-700" : "bg-blue-50 text-mint";
}

function jobStatusTone(status) {
  if (status === "Approved for Production") {
    return "bg-emerald-50 text-emerald-800";
  }

  if (status === "In Production") {
    return "bg-blue-50 text-mint";
  }

  if (status === "Artwork" || status === "Design" || status === "Proofing") {
    return "bg-amber-50 text-amber-900";
  }

  if (status === "Install Ready") {
    return "bg-sky-50 text-sky-800";
  }

  return "bg-slate-100 text-slate-700";
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
