import * as THREE from './vendor/three.module.js';
const $=s=>document.querySelector(s),$$=s=>[...document.querySelectorAll(s)];
let sequence=0,pending=new Map(),state=null,selected=0,checked=new Set(),group='',fields=[],busy=false,operation=0;
window.cflReply=reply=>{const p=pending.get(reply.id);if(!p)return;pending.delete(reply.id);reply.error?p.reject(Object.assign(new Error(reply.error),{issues:reply.issues})):p.resolve(reply.data);};
const replyParts=new Map();window.cflReplyPart=(id,index,total,part)=>{if(!pending.has(id)||index<0||index>=total||total>4096)return;let buffer=replyParts.get(id);if(!buffer){buffer={parts:new Array(total),remaining:total};replyParts.set(id,buffer);}if(buffer.parts[index]!==undefined)return;buffer.parts[index]=part;if(--buffer.remaining===0){replyParts.delete(id);try{window.cflReply(JSON.parse(buffer.parts.join('')));}catch(error){pending.get(id)?.reject(error);pending.delete(id);}}};
function call(command,data={}){return new Promise((resolve,reject)=>{const id=++sequence;pending.set(id,{resolve,reject});const request=JSON.stringify({id,command,data});if(window.CflNative)window.CflNative.postMessage(request);else if(window.webkit?.messageHandlers?.cfl)window.webkit.messageHandlers.cfl.postMessage(request);else{pending.delete(id);reject(new Error('请通过已安装的凯乐应用打开此界面。'));}});}
let messageTimer;function message(text,persistent=false){$('#message').textContent=text;$('#message').hidden=!text;clearTimeout(messageTimer);if(text&&!persistent)messageTimer=setTimeout(()=>$('#message').hidden=true,9000);}
const issuePanel = $('#calculation-issues');
function clearIssues(){
 if(!issuePanel)return;
 issuePanel.hidden=true;
 $('#issue-list').replaceChildren();
}
function showIssues(error, operationName){
 if(!issuePanel){message(error.message,true);return;}
 $('#calculation').textContent=operationName==='utility'?'公用工程计算未完成。':'计算未完成，不能生成图纸。';
 $('#issue-summary').textContent=error.message;
 const list=$('#issue-list');
 list.replaceChildren();
 for(const issue of error.issues??[]){
  const item=document.createElement('li');
  const label=document.createElement('strong');
  label.textContent=[issue.equipment||(issue.row!=null?'设备 '+(issue.row+1):'项目'),issue.field].join(' · ');
  const note=document.createElement('span');
  note.textContent=issue.message+' ('+issue.address+')';
  item.append(label,note);
  if(issue.group){
   const button=document.createElement('button');
   button.type='button';
   button.textContent='修改'+issue.field;
   button.onclick=()=>{
    if(Number.isInteger(issue.row)&&selected!==issue.row&&state?.rows.some(row=>row.index===issue.row)){
     selected=issue.row;
     $('#device').value=selected;
     $('#device').dispatchEvent(new Event('change'));
    }
    if(issue.group==='hydraulics'&&$('#pump-input')){
     window.dispatchEvent(new CustomEvent('cfl-focus-hydraulics',{detail:issue.address}));
     return;
    }
    return work(()=>showParameters(issue.group,issue.address));
   };
   item.append(button);
  }
  list.append(item);
 }
 issuePanel.hidden=false;
}
function updateControls(){
 $$('button:not([data-host-control]),select').forEach(button=>button.disabled=busy);
 if(!busy){
  $$('[data-job]').forEach(button=>button.disabled=!state?.job);
  $$('[data-equipment]').forEach(button=>button.disabled=!state?.rows.length);
  $('#generate').disabled=!state?.job||state.needsCalculation;
 }
 window.dispatchEvent(new CustomEvent('cfl-control-state',{detail:busy}));
}
async function work(action,{calculation=false}={}){
 if(busy)return;busy=true;const version=++operation;let polling=false;message('');if(calculation)clearIssues();$('#busy').hidden=false;$('#busyText').textContent=calculation?'正在计算项目数据……':'正在处理，请稍候…';updateControls();$('#cancelCalculation').hidden=!calculation;$('#cancelCalculation').disabled=false;
 const timer=calculation?setInterval(async()=>{if(polling)return;polling=true;try{const progress=await call('progress');if(busy&&version===operation){$('#log').textContent=progress.log.join('\n');$('#busyText').textContent=progress.log.at(-1)||'正在计算项目数据……';}}catch{}finally{polling=false;}},1000):null;
 try{return await action();}catch(e){
  if(calculation){
   showIssues(e,calculation);
   call('progress').then(progress=>{if(version===operation)$('#log').textContent=progress.log.join('\n');}).catch(()=>{});
  }else message(e.message);
 }
 finally{if(timer)clearInterval(timer);busy=false;$('#busy').hidden=true;$('#cancelCalculation').hidden=true;updateControls();}
}
$('#cancelCalculation').onclick=async()=>{const version=operation;$('#cancelCalculation').disabled=true;try{await call('cancel');if(busy&&version===operation)$('#busyText').textContent='正在取消计算并返回工作台……';}catch(error){if(busy&&version===operation){message(error.message);$('#cancelCalculation').disabled=false;}}};
const equipmentLabel = row => [row.tag, row.model].filter(Boolean).join(' · ');
function render(data){state=data;$('#project').textContent=data.job||'请选择或新建项目';$$('[data-job]').forEach(b=>b.disabled=!data.job);$('#generate').disabled=!data.job||data.needsCalculation;$('#calculation').textContent=data.needsCalculation?'修改参数后，请先计算项目数据。':'计算已完成，可以生成图纸。';$('#log').textContent=data.log.join('\n');$('#device').replaceChildren();for(const r of data.rows){const option=new Option(equipmentLabel(r),r.index);$('#device').append(option);}if(!data.rows.some(r=>r.index===selected))selected=data.rows[0]?.index??0;$('#device').value=selected;$('#rows').replaceChildren();for(const r of data.rows){const label=document.createElement('label');label.className='row';const input=document.createElement('input');input.type='checkbox';input.checked=checked.has(r.index);input.addEventListener('change',()=>input.checked?checked.add(r.index):checked.delete(r.index));label.append(input,document.createTextNode(equipmentLabel(r)));const detail=document.createElement('small');detail.textContent='SEC：'+(data.needsCalculation?'待计算':r.template||'待计算')+' · 材料清单：'+(data.needsCalculation?'待计算':r.list||'待计算');label.append(detail);$('#rows').append(label);}$('#files').replaceChildren();for(const f of data.files){const line=document.createElement('div');line.className='file';const title=document.createElement('strong');title.textContent=f.kind+' · '+f.name;const actions=document.createElement('div');actions.className='actions';for(const [label,action] of [['预览',()=>preview(f.id)],['导出 DWG',()=>call('export',{id:f.id,format:'dwg'})],['导出 PDF',()=>call('export',{id:f.id,format:'pdf'})]]){const button=document.createElement('button');button.textContent=label;button.onclick=()=>work(action);actions.append(button);}line.append(title,actions);$('#files').append(line);}window.dispatchEvent(new Event('cfl-data-rendered'));}
async function project(command,data={}){const result=await call(command,data);checked=new Set(result.rows.map(r=>r.index));render(result);}
window.addEventListener('cfl-new-from-pump', event => work(async () => {
 await project('new-from-pump', event.detail);
 message('已按选型创建项目。');
}));
window.addEventListener('cfl-new-from-batch', event => work(async () => {
 await project('new-from-batch', event.detail);
 message('已创建批量项目。');
}));
$('#new').onclick=()=>work(()=>project('new'));if($('#import'))$('#import').onclick=()=>work(()=>project('import'));$('#excel').onclick=()=>work(()=>call('export',{format:'xlsx'}));
$('#open').onclick=()=>work(async()=>{const projects=await call('projects');group='open';fields=[];$('#dialogTitle').textContent='打开项目';$('#fields').replaceChildren();$('#formError').textContent='';for(const entry of projects){const button=document.createElement('button');button.type='button';button.textContent=entry.name;button.onclick=()=>{$('#dialog').close();work(()=>project('open',{id:entry.id}));};$('#fields').append(button);}$('#form button[type=submit]').hidden=true;$('#dialog').showModal();});
$('#add').onclick=()=>work(()=>project('add'));
$('#remove').onclick=()=>{group='remove';fields=[];$('#dialogTitle').textContent='删除当前设备';$('#fields').textContent='确认从当前项目中删除所选设备？';$('#formError').textContent='';$('#form button[type=submit]').textContent='确认删除';$('#form button[type=submit]').textContent=group==='remove'?'确认删除':'保存参数';$('#form button[type=submit]').hidden=false;$('#dialog').showModal();};
$('#device').onchange=()=>selected=Number($('#device').value);
for(const button of $$('[data-form]'))button.onclick=()=>work(()=>showParameters(button.dataset.form));
async function showParameters(formGroup,address){
 group=formGroup;
 const form=await call('parameters',{group,row:selected});
 fields=form.fields;
 $('#dialogTitle').textContent=form.title;$('#fields').replaceChildren();$('#formError').textContent='';
 for(const [i,f] of fields.entries()){
  const label=document.createElement('label');label.className='field';label.textContent=f.label;
  const input=document.createElement(f.strictChoices?'select':'input');input.id='field'+i;
  if(f.strictChoices){
   input.append(new Option('请选择',''),...f.choices.map(choice=>new Option(choice)));
   if(f.value&&!f.choices.includes(f.value))input.append(new Option(f.value));
  }else{
   input.readOnly=!!f.readOnly;input.autocomplete='off';
   if(f.choices.length){
    const list=document.createElement('datalist');list.id='list'+i;
    list.append(...f.choices.map(choice=>new Option(choice)));
    input.setAttribute('list',list.id);label.append(list);
   }
  }
  input.value=f.value;label.append(input);$('#fields').append(label);
 }
 $('#form button[type=submit]').textContent='保存参数';
 $('#form button[type=submit]').hidden=false;$('#dialog').showModal();
 if(address){
  const index=fields.findIndex(f=>f.sheet+'!'+f.address===address);
  if(index>=0)$('#field'+index)?.focus();
 }
}
$('#print').onclick=()=>{group='print';fields=Object.entries(state.options).map(([key,value])=>({key,value}));const names={minX:'窗口左下 X',minY:'窗口左下 Y',maxX:'窗口右上 X',maxY:'窗口右上 Y',margin:'页边距（毫米）',copies:'份数'};$('#dialogTitle').textContent='A3 横向打印设置';$('#fields').replaceChildren();$('#formError').textContent='';for(const [i,f] of fields.entries()){const label=document.createElement('label');label.className='field';label.textContent=names[f.key];const input=document.createElement('input');input.id='field'+i;input.type='number';input.value=f.value;label.append(input);$('#fields').append(label);}$('#form button[type=submit]').hidden=false;$('#dialog').showModal();};
$('#cancel').onclick=()=>$('#dialog').close();$('#form').onsubmit=e=>{e.preventDefault();if(group==='open')return;work(async()=>{try{let result;if(group==='remove'){result=await call('remove',{row:selected});checked=new Set(result.rows.map(r=>r.index));}else if(group==='print'){const options=Object.fromEntries(fields.map((f,i)=>[f.key,Number($('#field'+i).value)]));result=await call('print',options);}else{const changes=fields.map((f,i)=>({...f,value:$('#field'+i).value})).filter((f,i)=>f.value!==fields[i].value).map(({sheet,address,value})=>({sheet,address,value}));result=await call('save',{group,row:selected,changes});}render(result);$('#dialog').close();message('参数已保存到项目 Excel。');}catch(error){$('#formError').textContent=error.message;}});};
$('#calculate').onclick=()=>work(async()=>{if(state)state.needsCalculation=true;$('#calculation').textContent='正在计算，请稍候……';render(await call('calculate'));message('计算完成，可以单击“生成 DWG＋PDF”。');},{calculation:'project'});$('#utility').onclick=()=>work(async()=>{render(await call('utility'));message('公用工程用量已保存到 Excel。');},{calculation:'utility'});
$('#generate').onclick=async()=>{
 let completed=null;
 await work(async()=>{const kinds=$$('.checks input:checked').map(c=>c.value);if(!kinds.length||!checked.size)throw new Error('请至少选择一台设备和一种图纸。');completed=await call('generate',{kinds,rows:[...checked]});render(completed);});
 if(completed){
  const count=completed.files.length, exported=completed.folderExport;
  let summary='已生成 '+count+' 组图纸，共 '+count+' 个 DWG 和 '+count+' 个 PDF 文件。';
  if(exported){
   summary+='\n已将 '+exported.saved+' 个 PDF 保存到工作目录：'+exported.folder+'。';
   if(exported.failed.length)summary+='\n未能保存：'+exported.failed.join('、')+'。请检查目录权限或空间，再从图纸列表导出。';
  }else summary+='\n可在图纸列表中预览或导出。';
  $('#creationSummary').textContent=summary;
  $('#creationComplete').showModal();
 }
};
$('#fit').onclick=()=>fit();
window.addEventListener('cfl-data-rendered',clearIssues);
async function preview(id){showDrawing(await call('scene',{id}));$('#viewport').scrollIntoView({behavior:'smooth',block:'center'});}
let renderer,scene,camera,content,frame,drawing,drag;const viewport=$('#viewport');
// The viewer implementation below is shared with the desktop scene format.
function initViewer(){
 renderer=new THREE.WebGLRenderer({antialias:true,alpha:false});renderer.setPixelRatio(Math.min(devicePixelRatio,2));renderer.setClearColor(0xe5ebef);viewport.append(renderer.domElement);scene=new THREE.Scene();camera=new THREE.OrthographicCamera(-230,230,165,-165,.1,2000);camera.position.set(210,148.5,1000);camera.lookAt(210,148.5,0);
 new ResizeObserver(()=>{renderer.setSize(viewport.clientWidth,viewport.clientHeight);fit(false);}).observe(viewport);
 renderer.domElement.addEventListener('wheel',e=>{e.preventDefault();camera.zoom=Math.min(25,Math.max(.15,camera.zoom*(e.deltaY<0?1.15:1/1.15)));camera.updateProjectionMatrix();renderScene();},{passive:false});
 const fingers=new Map();let previousSpan=null;
 renderer.domElement.addEventListener('pointerdown',e=>{fingers.set(e.pointerId,{x:e.clientX,y:e.clientY});drag={x:e.clientX,y:e.clientY};renderer.domElement.setPointerCapture(e.pointerId);});
 const release=e=>{fingers.delete(e.pointerId);previousSpan=null;drag=null;};renderer.domElement.addEventListener('pointerup',release);renderer.domElement.addEventListener('pointercancel',release);
 renderer.domElement.addEventListener('pointermove',e=>{if(!fingers.has(e.pointerId))return;fingers.set(e.pointerId,{x:e.clientX,y:e.clientY});if(fingers.size===2){const [p,q]=[...fingers.values()];const span=Math.hypot(p.x-q.x,p.y-q.y);if(previousSpan)camera.zoom=Math.min(25,Math.max(.15,camera.zoom*span/previousSpan));previousSpan=span;camera.updateProjectionMatrix();renderScene();return;}if(!drag)return;camera.position.x-=(e.clientX-drag.x)*(camera.right-camera.left)/camera.zoom/viewport.clientWidth;camera.position.y+=(e.clientY-drag.y)*(camera.top-camera.bottom)/camera.zoom/viewport.clientHeight;drag={x:e.clientX,y:e.clientY};renderScene();});
}
function renderScene(){if(renderer)renderer.render(scene,camera);}
function dispose(object){object.traverse(o=>{o.geometry?.dispose();if(o.material){for(const m of Array.isArray(o.material)?o.material:[o.material]){m.map?.dispose();m.dispose();}}});}
function fit(reset=true){if(!camera)return;const f=frame||{minX:0,minY:0,maxX:420,maxY:297},aspect=viewport.clientWidth/Math.max(1,viewport.clientHeight),h=Math.max(f.maxY-f.minY+24,(f.maxX-f.minX+24)/aspect);camera.left=-h*aspect/2;camera.right=h*aspect/2;camera.top=h/2;camera.bottom=-h/2;if(reset){camera.position.set((f.minX+f.maxX)/2,(f.minY+f.maxY)/2,1000);camera.zoom=1;}camera.updateProjectionMatrix();renderScene();}
function addDetailImage(item, group) {
 const [topLeft, topRight, bottomLeft] = item.points;
 const bottomRight = [topRight[0] + bottomLeft[0] - topLeft[0], topRight[1] + bottomLeft[1] - topLeft[1]];
 const geometry = new THREE.BufferGeometry();
 geometry.setAttribute('position', new THREE.Float32BufferAttribute(
  [topLeft, topRight, bottomLeft, bottomRight].flatMap(([x,y]) => [x,y,0]), 3));
 geometry.setAttribute('uv', new THREE.Float32BufferAttribute([0,1, 1,1, 0,0, 1,0], 2));
 geometry.setIndex([0,2,1, 2,3,1]);
 const material = new THREE.MeshBasicMaterial({depthTest:false, side:THREE.DoubleSide});
 const mesh = new THREE.Mesh(geometry, material);
 mesh.visible = false;
 mesh.renderOrder = -.5;
 material.map = new THREE.TextureLoader().load('data:image/png;base64,' + item.imageBase64, () => {
  if (content !== group) return;
  mesh.visible = true;
  renderScene();
 });
 material.map.colorSpace = THREE.SRGBColorSpace;
 group.add(mesh);
}
function showDrawing(data){
 drawing=data;frame=data.frame;if(!renderer)initViewer();if(content){scene.remove(content);dispose(content);}content=new THREE.Group();scene.add(content);
 const paper=new THREE.Mesh(new THREE.PlaneGeometry(frame.maxX-frame.minX,frame.maxY-frame.minY),new THREE.MeshBasicMaterial({color:0xffffff,depthTest:false}));paper.position.set((frame.minX+frame.maxX)/2,(frame.minY+frame.maxY)/2,-1);paper.renderOrder=-1;content.add(paper);
 const positions=[];
 for(const item of data.items){
  if(item.type==='image'){
   addDetailImage(item, content);
  }else if(item.type==='text'){
   const lines=(item.text||'').replaceAll('\r','').split('\n'),canvas=document.createElement('canvas'),ctx=canvas.getContext('2d'),px=40;ctx.font=px+'px "Microsoft YaHei",sans-serif';canvas.width=Math.min(4096,Math.max(20,...lines.map(s=>ctx.measureText(s).width))+12);canvas.height=Math.max(1,lines.length)*px*1.4;ctx.font=px+'px "Microsoft YaHei",sans-serif';ctx.fillStyle='#111';ctx.textBaseline='alphabetic';lines.forEach((s,i)=>ctx.fillText(s,4,px+i*px*1.3));const texture=new THREE.CanvasTexture(canvas),factor=item.widthFactor||1,w=canvas.width/px*item.height*factor,h=canvas.height/px*item.height;const glyphWidth=Math.max(...lines.map(s=>ctx.measureText(s).width))/px*item.height*factor;const offset=item.align==='center'?-glyphWidth/2:item.align==='right'?-glyphWidth:0;const text=new THREE.Mesh(new THREE.PlaneGeometry(w,h),new THREE.MeshBasicMaterial({map:texture,transparent:true,depthTest:false}));text.position.set(item.points[0][0],item.points[0][1],.1);text.rotation.z=item.angle;const child=new THREE.Group();child.position.copy(text.position);text.position.set(w/2-4/px*item.height*factor+offset,-h/2+item.height,.1);child.rotation.z=item.angle;text.rotation.z=0;child.add(text);content.add(child);
  }else if(item.type==='fill'&&item.points.length>=3){const shape=new THREE.Shape(item.points.map(p=>new THREE.Vector2(p[0],p[1])));const mesh=new THREE.Mesh(new THREE.ShapeGeometry(shape),new THREE.MeshBasicMaterial({color:0x111111,depthTest:false,side:THREE.DoubleSide}));content.add(mesh);
  }else{for(let i=1;i<item.points.length;i++)positions.push(...item.points[i-1],0,...item.points[i],0);if(item.closed&&item.points.length)positions.push(...item.points.at(-1),0,...item.points[0],0);}
 }
 const geometry=new THREE.BufferGeometry();geometry.setAttribute('position',new THREE.Float32BufferAttribute(positions,3));content.add(new THREE.LineSegments(geometry,new THREE.LineBasicMaterial({color:0x202c36,depthTest:false})));
 $('#empty').hidden=true;$('#canvasNote').hidden=false;fit();if(data.unsupported?.length)message('此模板有尚未支持的显示实体：'+data.unsupported.join('、')+'。PDF 导出会要求先检查模板。');
}

window.addEventListener('cfl-project-updated', event => render(event.detail));
work(async()=>render(await call('state')));
