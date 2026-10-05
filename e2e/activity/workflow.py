"""Real MVC + SignalR acceptance workflow. Run against a fresh sandbox app on :5119.
No fabricated audit rows: every displayed event comes from a business form/action.
E2E_SHOTS_DIR isolates dry runs from final evidence.
"""
from pathlib import Path
import json,os,re,sys,time
from playwright.sync_api import sync_playwright
ROOT=Path(__file__).resolve().parents[2];BASE='http://127.0.0.1:5119'
TAG=(sys.argv[1] if len(sys.argv)>1 else 'act')[:10]
SHOTS=Path(os.environ.get('E2E_SHOTS_DIR','/home/user/activity-dry'));SHOTS.mkdir(parents=True,exist_ok=True)
f=ROOT/'e2e/major-categories/workflow.py';m={'__file__':str(f)};exec(compile(f.read_text().split('\nwith sync_playwright()')[0],str(f),'exec'),m)
h=m['h'];h['TAG']=TAG;checks=[];sequence=0

def check(name,ok):
 assert ok,name
 checks.append(name);print(f'PASS {len(checks):02d}: {name}',flush=True)
def goto(p,path):
 r=p.goto(BASE+path,wait_until='domcontentloaded');p.wait_for_timeout(200);assert r and r.status<500,(path,r.status if r else None);return r
def submit(p,selector):
 with p.expect_navigation(wait_until='networkidle'):p.locator(selector).click()
def shot(p,label):
 global sequence
 sequence+=1;p.screenshot(path=str(SHOTS/f'{sequence:02d}-{label}.png'),full_page=False)
def text(p):return p.locator('#activityRows').inner_text()
def count(p,typ):return p.locator(f'[data-type="{typ}"]').count()
def wait_count(p,typ,n):p.wait_for_function('([t,n])=>document.querySelectorAll(`[data-type="${t}"]`).length===n',[typ,n],timeout=12000)
def post(p,path,fields):
 token=p.locator('input[name="__RequestVerificationToken"]').first.input_value()
 return p.request.post(BASE+path,form={'__RequestVerificationToken':token,**fields})
def create_staff(p,role,suffix):
 goto(p,'/admin/users/create');form=p.locator('form[action*="create"]')
 values={'FirstName':'Activity','LastName':suffix,'Email':f'{TAG}-{suffix}@example.com','Password':'StaffE2E!2026xY','Username':f'{TAG}.{suffix}'}
 for k,v in values.items():form.locator(f'[name="{k}"]').fill(v)
 form.locator('[name="RoleIds"]').select_option(label=role)
 submit(p,'form[action*="create"] button[type="submit"]');check(role+' account created','/create' not in p.url)
 return values['Email']
def login_staff(p,email):
 goto(p,'/Account/Login');p.locator('form[action*="Login"] [name="Email"]').fill(email);p.locator('form[action*="Login"] [name="Password"]').fill('StaffE2E!2026xY');submit(p,'form[action*="Login"] button[type="submit"]')
 if 'password' in p.url.lower():
  for k,v in {'CurrentPassword':'StaffE2E!2026xY','NewPassword':'StaffNew!2026xY','ConfirmPassword':'StaffNew!2026xY'}.items():p.locator(f'[name="{k}"]').fill(v)
  submit(p,'form[action*="password"] button[type="submit"]')
 check('Staff sign-in completed','/admin' in p.url)
def watch(p,frames):
 p.on('websocket',lambda ws:ws.on('framereceived',lambda payload:frames.append(payload) if isinstance(payload,str) and 'businessActivityChanged' in payload else None))
def live(p):
 goto(p,'/admin/activity');p.wait_for_function('document.getElementById("activityConnection").textContent==="Live"',timeout=15000)
def order_rows(p,order):goto(p,'/admin/activity?OrderId='+order);p.wait_for_timeout(500)

with sync_playwright() as pw:
 b=pw.chromium.launch();ctx=b.new_context(viewport={'width':1500,'height':1000});admin=ctx.new_page();admin.on('dialog',lambda d:d.accept());h['owner_login'](admin)
 cat=m['create_category'](admin,f'Activity category {TAG}')
 prod=m['create_product'](admin,f'Activity product {TAG}',cat,15000)
 sales=create_staff(admin,'SalesStaff','sales');manager_email=create_staff(admin,'Manager','manager')
 manager=b.new_page(viewport={'width':1500,'height':1000});login_staff(manager,manager_email)
 r=goto(manager,'/admin/activity');check('Manager default cannot read business activity',r.status==403 or 'AccessDenied' in manager.url or 'denied' in manager.inner_text('body').lower());shot(manager,'staff-access-denied')
 a=ctx.new_page();c2=b.new_context(viewport={'width':1500,'height':1000});bb=c2.new_page();h['owner_login'](bb);fa=[];fb=[];watch(a,fa);watch(bb,fb);live(a);live(bb);check('Two independently authenticated admin sessions are live',a.context!=bb.context);shot(a,'two-admin-live-feed')
 # Routine changes persist but do not interrupt live activity.
 before=len(fa);goto(admin,f'/admin/products/edit/{prod}');admin.locator('[name="SellingPrice"]').fill('16000');submit(admin,'form[action*="edit"] button[type="submit"]')
 a.wait_for_timeout(3000);check('Routine price edit does not push a live business signal',len(fa)==before);check('Historical-only price event waits for explicit refresh','Product price changed' not in text(a))
 a.locator('#activityRefresh').click();a.wait_for_selector('[data-type="product.price_changed"]');check('Refresh recovers persisted price change','15000' in text(a) and '16000' in text(a));shot(a,'historical-price-change')
 # Saving identical values must not create an additional semantic event.
 goto(admin,f'/admin/products/edit/{prod}');submit(admin,'form[action*="edit"] button[type="submit"]');a.locator('#activityRefresh').click();a.wait_for_timeout(700);check('Identical product edit is silent',count(a,'product.price_changed')==1)
 customer=b.new_page(viewport={'width':1500,'height':1000});h['register_customer'](customer,'activity');r=goto(customer,'/admin/activity');check('Customer cannot access the admin feed',r.status==403 or 'AccessDenied' in customer.url or 'denied' in customer.inner_text('body').lower())
 goto(customer,f'/portal/shop/{prod}/order');qty=customer.locator('[name="quantity"]');qty.fill('10001');submit_result=post(customer,f'/portal/shop/{prod}/order',{'quantity':'10001'});check('Invalid quantity is rejected without an order workspace redirect','/workspace' not in submit_result.url)
 # Show the real validation response without manufacturing an error page.
 customer.locator('form[action*="/order"]').evaluate('f=>f.noValidate=true');submit(customer,'form[action*="/order"] button[type="submit"]');check('Invalid quantity is visibly explained','Quantity must be between 1 and 10,000' in customer.inner_text('body'));shot(customer,'invalid-order-quantity')
 goto(customer,f'/portal/shop/{prod}/order');customer.locator('[name="quantity"]').fill('1');na=len(fa);nb=len(fb)
 submit(customer,'form[action*="/order"] button[type="submit"]');order=re.search(r'/portal/orders/([0-9a-f-]{36})',customer.url).group(1)
 a.wait_for_selector('[data-type="order.created"]',timeout=12000);bb.wait_for_selector('[data-type="order.created"]',timeout=12000)
 deadline=time.monotonic()+8
 while (len(fa)<=na or len(fb)<=nb) and time.monotonic()<deadline:a.wait_for_timeout(100)
 check('Order submission reaches both admin sessions through real SignalR hints',len(fa)>na and len(fb)>nb)
 check('One order-created row per admin, not timeline/notification duplicates',count(a,'order.created')==count(bb,'order.created')==1)
 check('Automatic sales assignment is recorded',count(a,'order.assigned')==1);shot(a,'live-order-and-assignment')
 check('Invalid submissions did not create false order success',count(a,'order.created')==1)
 # Design upload/approval through existing workspace controls.
 goto(customer,f'/portal/orders/{order}/workspace')
 forms=customer.locator('form[action*="designreview"]');print('design forms',forms.count(),flush=True)
 form=forms.first;form.locator('input[type="file"]').set_input_files('/home/user/e2e-dr-wip/dr_v1.png');submit(customer,'form[action*="designreview"] button[type="submit"]')
 a.wait_for_selector('[data-type="design.review_uploaded"]',timeout=12000);check('Design upload is a live business activity',count(a,'design.review_uploaded')==1)
 goto(admin,f'/admin/orders/{order}/workspace');h['expand_details'](admin);approve=admin.locator('form[action*="designreview"][action*="approve"]')
 if approve.count():submit(admin,'form[action*="designreview"][action*="approve"] button[type="submit"]')
 else:
  admin.locator('[data-design-review-approve], .js-dr-approve, button:has-text("Approve")').first.click();admin.wait_for_timeout(1800)
 a.wait_for_selector('[data-type="design.review_approved"]',timeout=12000);check('Design decision is recorded once',count(a,'design.review_approved')==1);shot(a,'live-design-decision')
 # Finalization + manual payment + complete production lifecycle.
 goto(admin,f'/admin/orders/{order}/workspace');submit(admin,'form[action*="inalize"] button[type="submit"]')
 a.wait_for_selector('[data-type="order.pricing_changed"]',timeout=12000);check('Finalized pricing is recorded',count(a,'order.pricing_changed')==1)
 goto(admin,f'/admin/orders/{order}');admin.select_option('form[action*="mark-paid"] select[name="Method"]','BankTransfer');admin.fill('form[action*="mark-paid"] input[name="Note"]','Private bank memo not for activity');submit(admin,'form[action*="mark-paid"] button[type="submit"]')
 a.wait_for_selector('[data-type="payment.succeeded"]',timeout=12000);check('Manual payment success appears live once',count(a,'payment.succeeded')==1);check('Free-text payment note stays out of activity','Private bank memo' not in text(a));shot(a,'live-payment-confirmed')
 for label,next_label in [('Start production','Mark ready'),('Mark ready','Mark completed'),('Mark completed','Completed')]:
  goto(admin,f'/admin/orders/{order}');submit(admin,f'button:has-text("{label}")');check(label+' persists',next_label in admin.inner_text('body'))
 order_rows(a,order);check('Completed order workflow retained in audit history','Completed' in text(a));shot(a,'completed-order-history')
 # Manual refund is a staff record, never a claim of gateway settlement.
 goto(admin,f'/admin/orders/{order}');submit(admin,'form[action*="refund"] button[type="submit"]');a.wait_for_selector('[data-type="payment.refund_recorded"]',timeout=12000)
 check('Manual refund wording is truthful','Manual refund recorded' in text(a) and 'not a gateway transfer confirmation' in text(a));shot(a,'manual-refund-recorded')
 goto(admin,f'/admin/orders/{order}');response=post(admin,f'/admin/orders/{order}/refund',{});check('Repeated refund cannot create another outcome','500' not in str(response.status));a.locator('#activityRefresh').click();a.wait_for_timeout(700);check('Repeated refund creates no duplicate success',count(a,'payment.refund_recorded')==1)
 # Reconnect recovery: change permissions while the independent admin is offline.
 c2.set_offline(True);goto(admin,'/admin/users/roles');row=admin.locator('tr').filter(has=admin.get_by_text('Manager',exact=True));perm_url=row.locator('a[href$="/permissions"]').get_attribute('href');goto(admin,perm_url)
 toggle=admin.locator('label').filter(has_text='activity.view').locator('input');check('Permission registry offers owner-delegable activity.view',toggle.count()==1 and not toggle.is_checked());toggle.check();shot(admin,'delegated-activity-permission');submit(admin,'form[action*="permissions"] button[type="submit"]')
 bb.wait_for_function('document.getElementById("activityConnection").textContent!=="Live"',timeout=45000)
 check('Offline session detected a real SignalR disconnect',bb.locator('#activityConnection').inner_text()!='Live')
 c2.set_offline(False);bb.wait_for_function('document.getElementById("activityConnection").textContent==="Live"',timeout=25000);bb.wait_for_selector('[data-type="role.permissions_changed"]',timeout=15000)
 check('Reconnect restores committed history missed while offline','activity.view' in text(bb));shot(bb,'reconnect-recovered-permission-change')
 live(manager);check('Delegated Manager can now read the feed with refreshed permission checks','Business activity' in manager.inner_text('body'))
 goto(admin,perm_url);admin.locator('label').filter(has_text='activity.view').locator('input').uncheck();submit(admin,'form[action*="permissions"] button[type="submit"]')
 manager.wait_for_function('document.getElementById("activityConnection").textContent==="Access unavailable"',timeout=25000);check('Revoked open session clears sensitive rows',manager.locator('.activity-entry').count()==0);shot(manager,'revocation-clears-open-feed')
 # Filters, safe links, clear validation, refresh durability, privacy.
 goto(a,'/admin/activity?OrderId='+order+'&Category=payments');check('Combined order/category filter is useful',a.locator('.activity-entry').count()==2 and 'Payment received' in text(a));shot(a,'filtered-payment-history')
 href=a.locator('a:has-text("Open order")').first.get_attribute('href');check('Entity link resolves to the existing order workspace',goto(admin,href).status==200 and order in admin.url)
 goto(a,'/admin/activity?Product='+f'Activity product {TAG}'+'&Type=product.price_changed');check('Human-readable product filter finds the precise price edit',count(a,'product.price_changed')==1)
 goto(a,'/admin/activity?Search=DOES-NOT-EXIST-'+TAG);check('Empty search has an explicit no-results state','No matching activity' in text(a));shot(a,'empty-filter-result')
 r=goto(a,'/admin/activity?From=2026-10-01&To=2026-01-01');check('Invalid date range returns visible validation and HTTP 400',r.status==400 and 'end date must not precede' in a.inner_text('body'));shot(a,'invalid-date-range')
 r=goto(a,'/admin/activity?OrderId=not-a-guid');check('Malformed ID rejected rather than broadening the query',r.status==400)
 order_rows(a,order);ids=a.locator('.activity-entry').evaluate_all('xs=>xs.map(x=>x.dataset.activityId)');a.reload(wait_until='networkidle');check('Refresh preserves durable activity IDs',ids==a.locator('.activity-entry').evaluate_all('xs=>xs.map(x=>x.dataset.activityId)'))
 check('No duplicate IDs after bursts and refresh',len(ids)==len(set(ids)));check('Feed contains no customer email, password, or verification-token fields',all(x not in text(a) for x in ['@example.com','PasswordHash','VerificationToken','Private bank memo']))
 shot(a,'final-order-audit-history');b.close()
print(json.dumps({'passed':len(checks),'checks':checks,'screenshots':sequence},indent=2))
