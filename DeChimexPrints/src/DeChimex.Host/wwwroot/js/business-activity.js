/* Persisted history is authoritative; transport delivers invalidation hints only. */
(function(){'use strict';
 const host=document.getElementById('activityRows');if(!host)return;
 const status=document.getElementById('activityConnection'),notice=document.getElementById('activityNotice');
 if(document.querySelector('.validation-summary-errors')){status.textContent='Check filters';document.getElementById('activityRefresh').disabled=true;return;}
 let cutoff=host.querySelector("[data-history-cutoff]").dataset.historyCutoff,timer,busy=false,pending=false,pendingFull=false,denied=false;
 function parseUtc(s){s=String(s||'');if(s.indexOf('T')>0&&!/(?:Z|[+-]\d{2}:?\d{2})$/.test(s))s+='Z';return Date.parse(s);}
 function ageText(s,title){const ms=parseUtc(s);if(!isFinite(ms))return title;const age=Math.max(0,Math.floor((Date.now()-ms)/1000));const ago=(n,u)=>n+' '+u+(n===1?'':'s')+' ago';return age<60?'Just now':age<3600?ago(Math.floor(age/60),'minute'):age<86400?ago(Math.floor(age/3600),'hour'):title;}
 function times(){host.querySelectorAll('time[datetime]').forEach(el=>{el.textContent=ageText(el.dateTime,el.title);});}
 async function refresh(full){
  if(denied)return;
  if(busy){pending=true;pendingFull=pendingFull||full;return;}
  if(!full&&Number(host.querySelector('[data-page]')?.dataset.page)>1){notice.textContent='New activity may be available. Refresh or return to page 1.';return;}
  busy=true;
  try{
   const url=new URL(location.href);url.searchParams.set('format','rows');
   if(full){url.searchParams.delete('HistoryCutoff');}else url.searchParams.set('HistoryCutoff',cutoff);
   const r=await fetch(url,{credentials:'same-origin',cache:'no-store',headers:{'X-Requested-With':'XMLHttpRequest'}});
   if(r.status===401||r.status===403||r.redirected){denied=true;host.replaceChildren();status.textContent='Access unavailable';notice.textContent='Your session or permission no longer permits access. Sign in again or contact the owner.';if(connection)await connection.stop();return;}
   if(!r.ok)throw new Error('Activity refresh failed');
   host.innerHTML=await r.text();if(full)cutoff=host.querySelector('[data-history-cutoff]').dataset.historyCutoff;notice.textContent='History checked '+new Date().toLocaleTimeString();times();
  }catch(e){notice.textContent='Connection interrupted. Persisted history will reload when the connection returns.';}
  finally{busy=false;if(pending){pending=false;const requestedFull=pendingFull;pendingFull=false;refresh(requestedFull);}}
 }
 function schedule(){clearTimeout(timer);timer=setTimeout(()=>refresh(false),250);}
 document.getElementById('activityRefresh').addEventListener('click',()=>refresh(true));
 let connection=null;
 async function start(){
  if(!window.signalR){status.textContent='History mode';return;}
  connection=new signalR.HubConnectionBuilder().withUrl('/hubs/notifications').withAutomaticReconnect([0,1000,3000,10000]).build();
  connection.on('businessActivityChanged',schedule);
  connection.onreconnecting(()=>status.textContent='Reconnecting…');
  connection.onreconnected(()=>{status.textContent='Live';refresh(true);});
  connection.onclose(()=>{if(!denied){status.textContent='History mode · retrying';setTimeout(connect,5000);}});
  async function connect(){if(denied)return;try{await connection.start();status.textContent='Live';refresh(true);}catch(e){status.textContent='History mode · retrying';setTimeout(connect,5000);}}
  await connect();
 }
 window.addEventListener('online',()=>refresh(true));
 document.addEventListener('visibilitychange',()=>{if(!document.hidden)refresh(false);});
 setInterval(()=>{times();if(!document.hidden)refresh(false);},15000);
 times();if(document.readyState==='complete')start();else window.addEventListener('load',start,{once:true});
})();
