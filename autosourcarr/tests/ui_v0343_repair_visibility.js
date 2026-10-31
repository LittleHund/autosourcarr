/* Execute real repair summary/rendering with controlled task and DOM inputs. */
const fs=require('fs'),path=require('path'),vm=require('vm'),assert=require('assert/strict');
const folder=path.join(__dirname,'../app/static'),app=fs.readFileSync(path.join(folder,'app.js'),'utf8');
const elements={};
for(const id of ['tasks','tasksGeneralPanel','tasksRepairsPanel','sourceRepairDetails','sourceRepairEvidence','taskRepairStatus','tasksSummary','taskNavBadge','repairTaskSectionCount',
 'repairTasksRunningList','repairTasksCompletedList','repairTasksFailedList'])elements[id]={textContent:'',hidden:false,open:false,
 addEventListener(){},classList:{contains:()=>true,toggle(){}}};
const context={console,document:{hidden:false,getElementById:id=>elements[id],querySelector:selector=>selector.includes('data-task-lane')?{}:null,
 querySelectorAll:()=>[],addEventListener(){}},MutationObserver:class{observe(){}},AbortController,
 $:selector=>elements[selector.slice(1)],renderTaskSection:(list,count,items)=>{context.sections[list]=items;},sections:{},
 taskNewestFirst:()=>0,workbenchState:{taskLane:'automatic',repairLane:'all'}};
context.window=context;vm.createContext(context);
const workbench=fs.readFileSync(path.join(folder,'workbench.js'),'utf8');
vm.runInContext(workbench.slice(workbench.indexOf('function workbenchTaskItems('),workbench.indexOf('function updateActivityStrip(')),context);
vm.runInContext(fs.readFileSync(path.join(folder,'repair-tasks.js'),'utf8'),context);
const job={id:'source-repair:'+'a'.repeat(32),repair_id:'a'.repeat(32),kind:'source-repair',state:'failed',repair_state:'blocked',
 context:'Example',message:'Original APK payload mismatch',origin:'manual',created_at:'2026-10-11T20:59:11Z'};
context.tasksState={items:[job],running:0,queued:0,waiting:0,source_repairs:{stale:false,isolation:{available:true}}};
const start=app.indexOf('function renderTasks(){'),end=app.indexOf('window.cancelTask=',start);
vm.runInContext(app.slice(start,end),context);context.renderTasks();
assert.equal(elements.repairTaskSectionCount.textContent,'1','Blocked repair must not look like an empty Repairs tab');
assert.equal(context.sections['#repairTasksFailedList'].length,1);
assert(elements.tasksSummary.textContent.includes('1 failed or blocked'));
assert(elements.taskRepairStatus.textContent.includes('1 failed or blocked'));
assert(!elements.taskRepairStatus.textContent.includes('Original APK payload mismatch'),'Job messages belong in their status section');
assert.equal(context.sections['#repairTasksRunningList'].length,0,'Blocked is not ongoing');
context.workbenchState.repairLane='automatic';context.renderTasks();
assert.equal(context.sections['#repairTasksFailedList'].length,0);
assert(!elements.taskRepairStatus.textContent.includes('Original APK payload mismatch'));
context.workbenchState.repairLane='all';
context.tasksState.items.push({...job,id:'source-repair:'+'b'.repeat(32),repair_id:'b'.repeat(32),state:'running',repair_state:'checking-website'});
context.renderTasks();assert.equal(context.sections['#repairTasksRunningList'].length,1);
assert.equal(context.sections['#repairTasksFailedList'].length,1,'General Automatic must not hide manual blocked repairs');
let diagnostic=context.taskUiDiagnosticContext();
assert.equal(diagnostic.all_filter_present,true);assert.equal(diagnostic.observed_blocked_jobs,1);
delete elements.repairTasksFailedList;context.document.querySelector=()=>null;
diagnostic=context.taskUiDiagnosticContext();assert.equal(diagnostic.repair_lists_present,false);assert.equal(diagnostic.all_filter_present,false);
assert(!JSON.stringify(diagnostic).includes('Example'),'UI diagnostics need counts, not source content');
console.log('Repair visibility: independent origins, actual status sections and missing-DOM diagnostics pass');
