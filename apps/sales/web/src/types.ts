export interface Health {
  status: string;
  databaseProvider: string;
  plannerHandoff: "configured" | "not-configured";
}

export interface AuthSession {
  authenticated: boolean;
  name: string | null;
  roles: string[];
  localDevelopment: boolean;
}

export interface PhaseEstimate {
  phase: string;
  taskCount: number;
  minHours: number;
  maxHours: number;
  isMonthly: boolean;
}

export interface WbsTask {
  wbsId: string;
  sequence: number;
  phase: string;
  workstream: string;
  name: string;
  deliverable: string;
  minHours: number;
  maxHours: number;
  durationMin: string;
  durationMax: string;
  supportHandoff: boolean;
}

export interface Estimate {
  taskCount: number;
  oneTimeHoursMin: number;
  oneTimeHoursMax: number;
  monthlyHoursMin: number;
  monthlyHoursMax: number;
  byPhase: PhaseEstimate[];
  tasks: WbsTask[];
}

export interface EngagementListItem {
  id: string;
  companyName: string;
  salesperson: string;
  meetingDate: string | null;
  createdUtc: string;
  oneTimeHoursMin: number;
  oneTimeHoursMax: number;
  monthlyHoursMin: number;
  monthlyHoursMax: number;
  taskCount: number;
  plannerHandoffStatus: string;
  deploymentSyncStatus?: string;
  ownerEmail?: string | null;
}

export interface Engagement extends EngagementListItem {
  customerId: string;
  contactName: string | null;
  contactEmail: string | null;
  users: number | null;
  endpoints: number | null;
  notes: string | null;
  plannerPlanId: string | null;
  plannerHandoffMessage: string | null;
    deploymentSyncMessage?: string | null;
    experience?: import("./Experience").ExperienceSelection | null;
  answers: Record<string, string[]>;
  byPhase: PhaseEstimate[];
  tasks: WbsTask[];
}

export interface DeploymentProfileSummary {
  customerId: string;
  customerName: string;
  contactName: string | null;
  contactEmail: string | null;
  engagementCount: number;
  latestEngagementId: string | null;
  latestEngagementUtc: string | null;
  profileId: string | null;
  tenantId: string | null;
  primaryDomain: string | null;
  reviewedControls: number;
  scorePercentage: number;
  createdUtc: string | null;
}

export interface DeploymentControl {
  controlId: string;
  name: string;
  category: string;
  deploymentPhase: string;
  phaseNumber: number;
  assessmentLevel: string;
  description: string;
  microsoftSolution: string;
  userImpact: string;
  recommendedState: string;
  validationMethod: string;
  sourceReference: string;
  pointValue: number;
  wbsId: string;
  dependencies: string[];
  status: string;
  notes: string | null;
  evidence: string | null;
  updatedUtc: string | null;
}

export interface DeploymentControlsResponse {
  controls: DeploymentControl[];
  score: {
    overallPercentage: number;
    totalControls: number;
    statusCounts: Record<string, number>;
  };
  scope: string;
  count: number;
  tenantChangesEnabled: false;
}

export interface DeploymentProfile {
  profileId: string | null;
  customerId: string;
  name: string;
  contactName: string | null;
  contactEmail: string | null;
  tenantId: string | null;
  primaryDomain: string | null;
  entraApplicationClientId: string | null;
  engagementCount: number;
  engagements: Array<{
    id: string;
    salesperson: string;
    meetingDate: string | null;
    createdUtc: string;
    oneTimeHoursMin: number;
    oneTimeHoursMax: number;
    monthlyHoursMin: number;
    monthlyHoursMax: number;
    plannerHandoffStatus: string;
    deploymentSyncStatus?: string;
    ownerEmail?: string | null;
  }>;
  review: {
    overallPercentage: number;
    totalControls: number;
    reviewedControls: number;
    statusCounts: Record<string, number>;
  };
  tenantChangesEnabled: false;
}

export interface AssessmentRunSummary {
  id: string;
  name: string;
  source: string;
  assessor: string;
  startedUtc: string;
  completedUtc: string | null;
  isComplete: boolean;
  overallPercentage: number;
  totalControls: number;
  implementedControls: number;
  partialControls: number;
  notImplementedControls: number;
  notAssessedControls: number;
  readinessRating: string;
}

export interface DeploymentFleet {
  customerCount: number;
  withCompletedAssessment: number;
  withoutCompletedAssessment: number;
  notice: string;
  tenantChangesEnabled: false;
  customers: Array<{
    customerId: string;
    customerName: string;
    latestCompletedAssessment: {
      id: string;
      name: string;
      source: string;
      catalogVersion: string;
      completedUtc: string;
      overallPercentage: number;
      totalControls: number;
      notAssessedControls: number;
      readinessRating: string;
    } | null;
  }>;
}

export interface AssessmentControlResult {
  controlId: string;
  name: string;
  category: string | null;
  deploymentPhase: string | null;
  pointValue: number;
  wbsId: string | null;
  status: string;
  notes: string | null;
  evidence: string | null;
  assessor: string;
  reviewedUtc: string;
  exceptionReason: string | null;
}

export interface AssessmentDetail {
  id: string;
  name: string;
  source: string;
  assessor: string;
  customerId: string;
  customerName: string | null;
  engagementId: string | null;
  engagementName: string | null;
  catalogVersion: string;
  startedUtc: string;
  completedUtc: string | null;
  isComplete: boolean;
  score: {
    overallScore: number;
    overallPercentage: number;
    totalControls: number;
    implementedControls: number;
    partialControls: number;
    notImplementedControls: number;
    notAssessedControls: number;
    exceptionControls: number;
    notApplicableControls: number;
    readinessRating: string;
    calculatedUtc: string;
  };
  breakdowns: Array<{
    breakdownType: string;
    label: string;
    percentage: number;
    totalPoints: number;
    earnedPoints: number;
    gapCount: number;
  }>;
  recommendations: Array<{ severity: string; text: string }>;
  criticalGaps: Array<{
    controlId: string;
    name: string;
    pointValue: number;
    status: string;
  }>;
  controls: AssessmentControlResult[];
  tenantChangesEnabled: false;
}

export interface AssessmentComparison {
  baseline: { id: string; name: string; completedUtc: string; overallPercentage: number };
  target: { id: string; name: string; completedUtc: string; overallPercentage: number };
  catalogVersion: string;
  controlCount: number;
  unchangedCount: number;
  scoreDifference: number;
  changes: Array<{ controlId: string; name: string; baselineStatus: string; targetStatus: string }>;
  notice: string;
  tenantChangesEnabled: false;
}

export interface SecurePlusDiscoveryQuestion {
  id: string;
  baselineReferences: string;
  phase: string;
  category: string;
  topic: string;
  question: string;
  rationale: string;
  answerType: string;
  suggestedAnswers: string;
  configurationImpact: string;
  decisionOwner: string;
  requiredParticipants: string;
  requiredBy: string;
  evidenceRequired: string;
  defaultIfUndecided: string;
  engineeringNotes: string;
  status: "NotStarted" | "Draft" | "Resolved" | "Deferred";
  answer: string | null;
  notes: string | null;
  updatedBy: string | null;
  updatedUtc: string | null;
}

export interface EngagementDiscovery {
  engagementId: string | null;
  customerId: string | null;
  catalogVersion: string;
  sourceStatus: string;
  totalQuestions: number;
  resolvedQuestions: number;
  draftQuestions: number;
  deferredQuestions: number;
  notStartedQuestions: number;
  tenantChangesEnabled: false;
  questions: SecurePlusDiscoveryQuestion[];
}

export interface SecurePlusCatalogSummary {
  catalogVersion: string;
  sourceStatus: string;
  discoveryQuestionCount: number;
  workItemCount: number;
  workItemHoursMin: number;
  workItemHoursMax: number;
  baselineSettingCount: number;
  targetModelTableCount: number;
  targetModelColumnCount: number;
  targetModelVersion: string;
  phases: Array<{ phase: string; taskCount: number; hoursMin: number; hoursMax: number }>;
  implementationModes: Array<{ mode: string; count: number }>;
  licenseFamilies: Array<{ family: string; count: number }>;
  sourceInventoryVersion: string;
  sourceCount: number;
  sourceAreas: Array<{ area: string; count: number }>;
  sourceStatuses: Array<{ status: string; count: number }>;
  tenantChangesEnabled: false;
  modelReferenceOnly: true;
}

export interface StandardsReferenceSource {
  id: string;
  area: string;
  name: string;
  fileName: string;
  format: string;
  sourceVersion: string;
  status: string;
  usageBoundary: string;
  description: string;
  importedRecordCount: number | null;
  recordUnit: string | null;
}

export interface SecurePlusWorkItem {
  id: string;
  phaseReference: string;
  phase: string;
  workstream: string;
  taskNumber: number;
  taskName: string;
  subTaskNumber: string;
  subTaskName: string;
  control: string;
  controlDescription: string;
  deliverable: string;
  dependency: string;
  appliesWhen: string;
  licenseRequirement: string;
  estimatedHoursMin: number;
  estimatedHoursMax: number;
  durationDaysMin: number;
  durationDaysMax: number;
  parallelizable: boolean;
  supportHandoff: boolean;
  complexityDriver: string;
}

export interface SecurePlusBaselineSetting {
  id: string;
  baselineReference: string;
  phase: string;
  workstream: string;
  control: string;
  settingCategory: string;
  settingName: string;
  targetAction: string;
  reviewPath: string;
  deploymentMethod: string;
  validationMethod: string;
  evidenceRequired: string;
  appliesWhen: string;
  licenseRequirement: string;
  implementationMode: string;
  pilotGroup: string;
  exclusions: string;
  exceptionCriteria: string;
  rollbackMethod: string;
  reviewCadence: string;
  supportOwner: string;
}

export interface TargetModelReference {
  modelReferenceOnly: true;
  catalogVersion: string;
  sourceStatus: string;
  count: number;
  tables: Array<{
    schema: string;
    name: string;
    description: string;
    estimatedRows: string;
    primarySources: string;
    temporalHistory: string;
    columns: Array<{
      columnOrdinal: number;
      name: string;
      dataType: string;
      isPrimaryKey: boolean;
      isForeignKey: boolean;
      foreignKeyTarget: string | null;
      isUniqueKeyPart: boolean;
      isIndexed: boolean;
    }>;
  }>;
}

export interface DeploymentChangePlan {
  id: string;
  customerId: string;
  customerName: string | null;
  assessmentRunId: string;
  title: string;
  notes: string | null;
  status: string;
  requestedById: string;
  requestedBy: string;
  createdUtc: string;
  updatedUtc: string;
  submittedUtc: string | null;
  reviewedById: string | null;
  reviewedBy: string | null;
  reviewedUtc: string | null;
  reviewDecision: string | null;
  reviewNotes: string | null;
  items: Array<{
    controlId: string;
    controlName: string;
    assessmentStatus: string;
    sequence: number;
  }>;
  events: Array<{
    eventType: string;
    actor: string;
    notes: string | null;
    occurredUtc: string;
  }>;
  executionEnabled: false;
  executionBlockReason: string;
}

export type IntakeAnswers = Record<string, string[]>;
