"""Real customer wishlist workflow. Use a fresh :5119 app and existing local Brevo stub.
E2E_SHOTS_DIR isolates each attempt. Reuses existing catalog and registration helpers.
"""
from pathlib import Path
import os,sys,json,uuid
from urllib.parse import urlparse,parse_qs
from playwright.sync_api import sync_playwright
ROOT=Path(__file__).resolve().parents[2];BASE='http://127.0.0.1:5119';TAG=(sys.argv[1] if len(sys.argv)>1 else 'wish')[:10]
SHOTS=Path(os.environ.get('E2E_SHOTS_DIR','/home/user/wishlist-dry'));SHOTS.mkdir(parents=True,exist_ok=True)
f=ROOT/'e2e/major-categories/workflow.py';m={'__file__':str(f)};exec(compile(f.read_text().split('\nwith sync_playwright()')[0],str(f),'exec'),m);h=m['h'];h['TAG']=TAG
checks=[];seq=0

def check(name,ok):
 assert ok,name
 checks.append(name);print(f'PASS {len(checks):02d}: {name}',flush=True)
def goto(p,path):
 r=p.goto(BASE+path,wait_until='domcontentloaded');p.wait_for_timeout(250);assert r and r.status<500;return r
def submit(p,selector):
 with p.expect_navigation(wait_until='domcontentloaded'):p.locator(selector).click()
 p.wait_for_timeout(250)
def shot(p,label):
 global seq
 seq+=1;p.screenshot(path=str(SHOTS/f'{seq:02d}-{label}.png'),full_page=False)
def button(p,id):return p.locator(f'form.wishlist-form[data-product-id="{id}"] button')
def toggle(p,id):
 with p.expect_navigation(wait_until='domcontentloaded'):button(p,id).click()
 p.wait_for_timeout(250)
def post(p,path,fields=None):
 token=p.locator('input[name="__RequestVerificationToken"]').first.input_value()
 return p.request.post(BASE+path,form={'__RequestVerificationToken':token,**(fields or {})},max_redirects=0)

with sync_playwright() as pw:
 b=pw.chromium.launch();admin=b.new_page(viewport={'width':1500,'height':1000});admin.on('dialog',lambda d:d.accept());h['owner_login'](admin)
 cat=m['create_category'](admin,'Wish category '+TAG)
 custom=m['create_product'](admin,'Wish custom '+TAG,cat,15000)
 plain=m['create_product'](admin,'Wish plain '+TAG,cat,10000)
 retired=m['create_product'](admin,'Retired item '+TAG,cat,9000)
 goto(admin,'/admin/products/edit/'+plain);admin.locator('input[name="IsCustomizable"][type="checkbox"]').uncheck();submit(admin,'form[action*="edit"] button[type="submit"]')
 a=b.new_page(viewport={'width':1500,'height':1000});email=h['register_customer'](a,'wish')
 filtered=f'/portal/shop?categoryId={cat}&search=Wish&sort=low&page=1';goto(a,filtered)
 check('Shop cards expose visible add controls for both product types',button(a,custom).inner_text().strip()=='Add to wishlist' and button(a,plain).inner_text().strip()=='Add to wishlist')
 check('Wishlist forms are outside product navigation links',a.locator('a form.wishlist-form').count()==0);shot(a,'shop-add-to-wishlist-controls')
 toggle(a,custom);check('Saving from catalog retains category/search/sort/page',parse_qs(urlparse(a.url).query)==parse_qs(urlparse(filtered).query))
 check('Saved state and success feedback shown',button(a,custom).get_attribute('aria-pressed')=='true' and 'Added to your wishlist.' in a.inner_text('body'));shot(a,'saved-state-filter-preserved')
 # Deliberately replay the ADD, rather than toggle; no duplicate item should result.
 for _ in range(2):check('Repeated add is accepted idempotently',post(a,f'/portal/wishlist/add/{custom}').status==302)
 goto(a,'/portal/wishlist');check('Only one saved card after replay',a.locator(f'form[action="/portal/wishlist/remove/{custom}"]').count()==1)
 check('Wishlist opens products within the portal',a.locator('a:has-text("View product / order")').first.get_attribute('href')==f'/portal/shop/{custom}');shot(a,'wishlist-saved-product-and-order-link')
 submit(a,'a:has-text("View product / order")');check('Product detail shows remove state and original order action',button(a,custom).inner_text().strip()=='Remove from wishlist' and a.get_by_text('Order / Customize',exact=True).count()==1)
 toggle(a,custom);check('Remove on detail returns to same product with add state',urlparse(a.url).path==f'/portal/shop/{custom}' and button(a,custom).get_attribute('aria-pressed')=='false')
 toggle(a,custom);a.reload(wait_until='domcontentloaded');check('Re-add after soft removal persists on refresh',button(a,custom).get_attribute('aria-pressed')=='true');shot(a,'product-detail-restored-favourite')
 goto(a,'/products/'+custom);check('Existing public detail reflects portal saved state','Saved' in a.inner_text('body'))
 second=b.new_page(viewport={'width':1500,'height':1000});h['register_customer'](second,'other');goto(second,'/portal/wishlist');check('Another customer cannot see first customer favourites','Wish custom '+TAG not in second.inner_text('body'))
 check('Forged remove is scoped to its authenticated customer',post(second,f'/portal/wishlist/remove/{custom}').status==302)
 goto(a,'/portal/wishlist');check('Other customer removal did not touch owner favourite','Wish custom '+TAG in a.inner_text('body'))
 fresh=b.new_page(viewport={'width':1500,'height':1000});goto(fresh,'/Account/Login');fresh.locator('form[action*="Login"] [name="Email"]').fill(email);fresh.locator('form[action*="Login"] [name="Password"]').fill('CustE2E!2026xY');submit(fresh,'form[action*="Login"] button[type="submit"]');goto(fresh,'/portal/wishlist');check('New authenticated session recovers stored wishlist','Wish custom '+TAG in fresh.inner_text('body'))
 # Reuse existing customer flow all the way from a saved custom product to an order.
 goto(a,'/portal/shop/'+custom);submit(a,'a:has-text("Order / Customize")');a.locator('[name="quantity"]').fill('1');submit(a,'form[action*="/order"] button[type="submit"]');check('Saved custom product can still submit an order request','/portal/orders/' in a.url and '/workspace' in a.url);shot(a,'saved-custom-product-order-submitted')
 goto(a,'/portal/shop/'+plain);toggle(a,plain);a.locator('[name="quantity"]').fill('2');submit(a,'form[action*="add-to-cart"] button[type="submit"]');check('Saved plain product still enters cart with correct quantity','Wish plain '+TAG in a.inner_text('body') and a.locator('input[name="quantity"]').first.input_value()=='2')
 goto(a,'/portal/wishlist');check('Ordering does not remove independent favourites',a.locator('form[action*="/portal/wishlist/remove/"]').count()==2)
 # Transport and redirect boundaries.
 check('Missing antiforgery token rejected',a.request.post(BASE+f'/portal/wishlist/add/{plain}',form={},max_redirects=0).status==400)
 check('Unknown product rejected',post(a,f'/portal/wishlist/add/{uuid.uuid4()}').status==404)
 response=post(a,f'/portal/wishlist/add/{plain}',{'returnUrl':'https://example.com/unsafe'});check('External redirect blocked',response.headers.get('location')=='/portal/wishlist')
 guest=b.new_page();response=goto(guest,'/portal/shop');check('Anonymous portal access requires sign-in','/Account/Login' in guest.url)
 goto(guest,'/products/'+custom);check('Public anonymous product keeps sign-in-to-save prompt','Sign in to save' in guest.inner_text('body'));guest.close()
 # A stale product page cannot save a deleted product; capture the real error page.
 stale=b.new_page(storage_state=a.context.storage_state(),viewport={'width':1500,'height':1000});goto(stale,'/portal/shop/'+retired)
 goto(admin,'/admin/products?search=Retired');response=post(admin,'/admin/products/delete/'+retired);check('Catalog product deletion succeeds',response.status==302)
 toggle(stale,retired);check('Stale add receives a visible not-found page','not found' in stale.inner_text('body').lower() or '404' in stale.inner_text('body'));shot(stale,'deleted-product-cannot-be-saved')
 goto(a,'/portal/wishlist');check('Rejected save did not create a wishlist entry','Retired item '+TAG not in a.inner_text('body'))
 # Mobile discoverability and keyboard activation.
 a.set_viewport_size({'width':390,'height':844});goto(a,filtered);button(a,plain).scroll_into_view_if_needed()
 check('Mobile wishlist labels fit within both product cards',a.locator('form.wishlist-form button').evaluate_all('xs=>xs.every(x=>x.getBoundingClientRect().right<=x.closest("article").getBoundingClientRect().right && x.querySelector("span").scrollWidth<=x.querySelector("span").clientWidth+1)'))
 shot(a,'mobile-wishlist-control')
 with a.expect_navigation(wait_until='domcontentloaded'):button(a,plain).focus();button(a,plain).press('Enter')
 check('Wishlist control is keyboard operable on mobile layout',button(a,plain).get_attribute('aria-pressed')=='false')
 a.set_viewport_size({'width':1500,'height':1000});goto(a,'/portal/wishlist');submit(a,f'form[action="/portal/wishlist/remove/{custom}"] button')
 check('Wishlist remove leads to empty state','Your wishlist is empty' in a.inner_text('body'))
 check('Empty-state browse link stays in portal',a.get_by_role('link',name='Browse products',exact=True).get_attribute('href')=='/portal/shop');shot(a,'empty-wishlist-browse-portal')
 submit(a,'a:has-text("Browse products")');check('Customer can immediately browse and save again',button(a,plain).get_attribute('aria-pressed')=='false');b.close()
print(json.dumps({'passed':len(checks),'screenshots':seq,'checks':checks},indent=2))
