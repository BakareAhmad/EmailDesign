"""Real MVC browser workflow; run on a fresh sandbox app + local Brevo stub.
Usage: python e2e/major-categories/workflow.py <unique-run-tag>
Screenshots go to E2E_SHOTS_DIR, default evidence/ (never mix runs).
"""
from pathlib import Path
import json, os, re, sys
from playwright.sync_api import sync_playwright

ROOT=Path(__file__).resolve().parents[2]
BASE='http://127.0.0.1:5119'
TAG=(sys.argv[1] if len(sys.argv)>1 else 'major')[:10]
SHOTS=Path(os.environ.get('E2E_SHOTS_DIR',str(ROOT/'evidence')))
SHOTS.mkdir(parents=True,exist_ok=True)
# Reuse proven login/registration helpers without executing their separate journey.
helperfile=ROOT/'e2e/design-review/ws_designreview_e2e.py'
h={'__file__':str(helperfile)}
exec(compile(helperfile.read_text().split('\nwith sync_playwright() as p:')[0],str(helperfile),'exec'),h)
h['TAG']=TAG
checks=[]; seq=0

def check(name,condition):
    assert condition,name
    checks.append(name);print(f'PASS {len(checks):02d}: {name}',flush=True)
def goto(p,path):
    r=p.goto(BASE+path,wait_until='networkidle'); assert r and r.status<500,(path,r.status if r else None)
    return r

def shot(p,label):
    global seq
    seq+=1;p.screenshot(path=str(SHOTS/f'{seq:02d}-{label}.png'),full_page=True)
    print(f'SCREENSHOT {seq:02d} {label}',flush=True)
def submit(p,selector):
    with p.expect_navigation(wait_until='networkidle'):
        p.locator(selector).click()

def formpost(p,url,fields):
    token=p.locator('input[name="__RequestVerificationToken"]').first.input_value()
    return p.request.post(BASE+url,form={'__RequestVerificationToken':token,**fields})
def grid(p):
    return p.locator('#grid').inner_text() if p.locator('#grid').count() else ''

def create_category(p,name,major=''):
    goto(p,'/admin/categories/create')
    p.locator('input[name="Name"]').fill(name)
    p.locator('select[name="MajorCategoryId"]').select_option(major)
    submit(p,'form[action*="create"] button[type="submit"]')
    row=p.locator('tr').filter(has=p.get_by_text(name,exact=True))
    href=row.locator('a:has-text("Edit")').get_attribute('href')
    check('category created: '+name, bool(href))
    return href.split('/')[-1]
def create_product(p,name,cat,price):
    goto(p,'/admin/products/create')
    form=p.locator('form[action*="create"]')
    form.locator('[name="Name"]').fill(name)
    form.locator('[name="CategoryId"]').select_option(cat)
    form.locator('[name="SellingPrice"]').fill(str(price))
    form.locator('[name="InitialQuantity"]').fill('50')
    form.locator('[name="IsCustomizable"][type="checkbox"]').check()
    form.locator('[name="Images"]').set_input_files('/home/user/e2e-dr-wip/product.png')
    submit(p,'form[action*="create"] button[type="submit"]')
    check('product created with category: '+name,name in p.inner_text('body'))
    goto(p,"/admin/products?search="+name)
    row=p.locator('tr').filter(has=p.get_by_text(name,exact=True)).first
    return row.locator('a[href*="/edit/"]').get_attribute('href').split('/')[-1]
def create_major(p,name,desc=''):
    goto(p,'/admin/major-categories/create')
    p.locator('[name="Name"]').fill(name);p.locator('[name="Description"]').fill(desc)
    submit(p,'form[action*="create"] button[type="submit"]')
    check('major created: '+name,'/create' not in p.url and name in p.inner_text('body'))
    return p.url.split('/')[-1]
def assign(p,cat,major):
    goto(p,f'/admin/categories/edit/{cat}')
    p.locator('[name="MajorCategoryId"]').select_option(major)
    submit(p,'form[action*="edit"] button[type="submit"]')
    goto(p,f'/admin/categories/edit/{cat}')
    check('category assignment persisted',p.locator('[name="MajorCategoryId"]').input_value()==major)
def options(p,cat):
    goto(p,f'/admin/categories/{cat}/options')
    p.locator('.opt-row .opt-question').first.fill('Personalisation text')
    p.locator('.opt-row .opt-type').first.select_option('Text')
    p.locator('.opt-row .opt-required').first.check()
    submit(p,'#saveForm button[type="submit"]')
def order(p,pid,text):
    goto(p,f'/portal/shop/{pid}/order')
    check('category ordering question retained','Personalisation text' in p.inner_text('body'))
    p.locator('[name^="opt_"]').first.fill(text)
    submit(p,'form[action*="/order"] button[type="submit"]')
    if '/workspace' not in p.url:
        p.locator('a[href*="/workspace"]').first.click();p.wait_for_load_state('networkidle')
    check('order completed through persisted workspace','/workspace' in p.url and text in p.inner_text('body'))
    return p.url

with sync_playwright() as pw:
    browser=pw.chromium.launch()
    staff=browser.new_page(viewport={'width':1440,'height':1050})
    public=browser.new_page(viewport={'width':1440,'height':1050})
    customer=browser.new_page(viewport={'width':1440,'height':1050})
    h['owner_login'](staff)
    # Existing leaf/product work before any major is introduced.
    mugs=create_category(staff,'Mugs '+TAG);options(staff,mugs)
    mug_name='Magic Mug '+TAG;mug=create_product(staff,mug_name,mugs,5000)
    goto(public,'/products?categoryId='+mugs)
    check('unassigned legacy category link and product work',mug_name in grid(public) and 'Other categories' in public.inner_text('body'))
    shot(public,'existing-unassigned-category-product')
    goto(staff,'/admin/major-categories/create')
    staff.locator('form[action*="create"] button[type="submit"]').click()
    check('major required name blocked in browser',not staff.locator('[name="Name"]').evaluate('(e)=>e.checkValidity()'))
    shot(staff,'required-name-validation')
    drink=create_major(staff,'Drinkware '+TAG)
    shot(staff,'major-created-name-only')
    apparel=create_major(staff,'Apparel Wear '+TAG,'Personalised clothing and team wear')
    shot(staff,'major-created-with-description')
    goto(staff,'/admin/major-categories/create');staff.locator('[name="Name"]').fill('  drinkware '+TAG+'  ')
    submit(staff,'form[action*="create"] button[type="submit"]')
    check('case-whitespace duplicate rejected','already exists' in staff.inner_text('body'))
    shot(staff,'duplicate-major-rejected')
    assign(staff,mugs,drink);shot(staff,'existing-subcategory-assigned')
    flasks=create_category(staff,'Flasks '+TAG,drink)
    jerseys=create_category(staff,'Jerseys '+TAG,apparel)
    flask_name='Stainless Steel Flask '+TAG;flask=create_product(staff,flask_name,flasks,12000)
    jersey_name='Barcelona Jersey '+TAG;jersey=create_product(staff,jersey_name,jerseys,18000)
    goto(staff,'/admin/major-categories/'+drink)
    check('major shows associated subcategories',all(n+' '+TAG in staff.inner_text('body') for n in ['Mugs','Flasks']))
    shot(staff,'major-with-associated-subcategories')
    goto(staff,'/admin/categories?unassigned=true')
    check('assigned subcategories absent from unassigned view','Mugs '+TAG not in staff.locator('tbody').inner_text())
    shot(staff,'unassigned-admin-filter')
    goto(staff,f'/admin/products/edit/{mug}')
    check('product edit shows hierarchy',('Drinkware '+TAG+' → Mugs '+TAG) in staff.locator('[name="CategoryId"]').inner_text())
    staff.locator('[name="SellingPrice"]').fill('6500')
    submit(staff,'form[action*="edit"] button[type="submit"]')
    goto(staff,f'/admin/products/edit/{mug}')
    check('product edit retained category and persisted price',staff.locator('[name="CategoryId"]').input_value()==mugs and float(staff.locator('[name="SellingPrice"]').input_value())==6500)
    shot(staff,'existing-product-edit-preserved')
    # Public navigation through rendered links, not only constructed URLs.
    goto(public,'/products')
    public.locator('#cats').get_by_text('Drinkware '+TAG,exact=True).click();public.wait_for_load_state('networkidle')
    check('major filter includes both leaves and excludes unrelated products',mug_name in grid(public) and flask_name in grid(public) and jersey_name not in grid(public))
    shot(public,'public-major-discovery')
    public.locator('#cats').get_by_text('Mugs '+TAG,exact=True).click();public.wait_for_load_state('networkidle')
    check('combined navigation filters intersect',mug_name in grid(public) and flask_name not in grid(public) and f'majorCategoryId={drink}' in public.url and f'categoryId={mugs}' in public.url)
    shot(public,'public-combined-subcategory-filter')
    public.locator('[name="search"]').fill('Magic')
    submit(public,'form[aria-label="Product search"] button[type="submit"]')
    check('search preserves both filters',mug_name in grid(public) and f'majorCategoryId={drink}' in public.url and f'categoryId={mugs}' in public.url)
    with public.expect_navigation(wait_until='networkidle'):
        public.locator('[name="sort"]').select_option('high')
    check('sort preserves both filters and search','search=Magic' in public.url and 'sort=high' in public.url and f'majorCategoryId={drink}' in public.url)
    shot(public,'search-and-sort-retain-hierarchy')
    goto(public,f'/products?categoryId={mugs}')
    check('old category-only URL still works',mug_name in grid(public) and flask_name not in grid(public))
    goto(public,f'/products?majorCategoryId={apparel}&categoryId={mugs}')
    check('mismatched combination returns no unrelated products',grid(public)=='' and 'does not belong' in public.inner_text('body'))
    shot(public,'conflicting-filters-empty-state')
    check('invalid major ID input is rejected',goto(public,'/products?majorCategoryId=invalid').status==400)
    goto(public,'/products?majorCategoryId=00000000-0000-0000-0000-000000000001')
    check('unknown major never broadens results',grid(public)=='' and 'No products found' in public.inner_text('body'))
    h['register_customer'](customer,'shop')
    goto(customer,'/portal/shop')
    customer.locator('#cats').get_by_text('Drinkware '+TAG,exact=True).click();customer.wait_for_load_state('networkidle')
    check('portal major navigation works',mug_name in grid(customer) and flask_name in grid(customer) and jersey_name not in grid(customer))
    shot(customer,'portal-major-shopping')
    customer.locator('#cats').get_by_text('Mugs '+TAG,exact=True).click();customer.wait_for_load_state('networkidle')
    check('portal combined filter works',mug_name in grid(customer) and flask_name not in grid(customer))
    shot(customer,'portal-subcategory-shopping')
    # Category order input validation then actual successful order.
    goto(customer,f'/portal/shop/{mug}/order')
    customer.locator('form[action*="/order"] button[type="submit"]').click();customer.wait_for_timeout(500)
    check('required category option cannot submit empty','/order' in customer.url and '/workspace' not in customer.url)
    shot(customer,'ordering-option-required-validation')
    first_order=order(customer,mug,'First mug before move')
    shot(customer,'order-created-before-move')
    # Authorization, anti-forgery and in-use deletion.
    response=customer.request.get(BASE+'/admin/major-categories/create')
    check('customer cannot manage major categories',response.status==403 or 'AccessDenied' in response.url)
    goto(customer,'/admin/major-categories/create');shot(customer,'customer-management-denied')
    goto(staff,'/admin/major-categories')
    csrf=staff.request.post(BASE+'/admin/major-categories/create',form={'Name':'No token'})
    check('missing antiforgery rejected',csrf.status==400)
    row=staff.locator('tr').filter(has=staff.get_by_text('Drinkware '+TAG,exact=True))
    submit(staff,f'form[action="/admin/major-categories/delete/{drink}"] button')
    check('major deletion with children rejected','Move or unassign' in staff.inner_text('body'))
    shot(staff,'major-in-use-deletion-blocked')
    # Category order is not changed by reassignment.
    goto(staff,'/admin/categories');row=staff.locator('tr').filter(has=staff.get_by_text('Mugs '+TAG,exact=True));before=row.locator('td').nth(4).inner_text()
    assign(staff,mugs,apparel);shot(staff,'subcategory-moved-to-another-major')
    goto(staff,'/admin/categories');row=staff.locator('tr').filter(has=staff.get_by_text('Mugs '+TAG,exact=True))
    check('moving keeps category ordering',row.locator('td').nth(4).inner_text()==before)
    goto(public,'/products?majorCategoryId='+drink)
    check('old group immediately loses moved category products',mug_name not in grid(public) and flask_name in grid(public))
    goto(public,'/products?majorCategoryId='+apparel)
    check('new group immediately gains moved category products',mug_name in grid(public) and jersey_name in grid(public))
    shot(public,'moved-products-derived-from-category')
    goto(customer,first_order.removeprefix(BASE))
    check('existing order history survives move','First mug before move' in customer.inner_text('body'))
    second_order=order(customer,mug,'Second mug after move');shot(customer,'order-created-after-move')
    goto(staff,'/admin/categories')
    staff.on('dialog',lambda d:d.accept())
    submit(staff,f'form[action="/admin/categories/delete/{mugs}"] button')
    check('category with products deletion still blocked','still has products' in staff.inner_text('body'))
    shot(staff,'product-bearing-category-deletion-blocked')
    # Empty deletion + unassign are valid; invalid parent is rejected server-side.
    spare=create_category(staff,'Temporary '+TAG,drink)
    goto(staff,'/admin/categories')
    submit(staff,f'form[action="/admin/categories/delete/{spare}"] button')
    check('empty category deletion succeeds','Temporary '+TAG not in staff.locator('tbody').inner_text())
    assign(staff,flasks,'')
    goto(staff,'/admin/categories?unassigned=true')
    check('unassigned category now listed','Flasks '+TAG in staff.locator('tbody').inner_text())
    shot(staff,'unassignment-and-unassigned-view')
    goto(staff,'/admin/major-categories')
    submit(staff,f'form[action="/admin/major-categories/delete/{drink}"] button')
    check('empty major deletion succeeds','Drinkware '+TAG not in staff.locator('tbody').inner_text())
    shot(staff,'empty-major-deleted')
    goto(staff,'/admin/categories/edit/'+flasks)
    invalid=formpost(staff,'/admin/categories/edit/'+flasks,{'Name':'Flasks '+TAG,'MajorCategoryId':drink,'IsActive':'true'})
    check('deleted major cannot be reassigned','Select an existing major category' in invalid.text())
    goto(public,'/products?majorCategoryId='+drink)
    check('deleted major link shows no products',grid(public)=='')
    goto(public,'/products?categoryId='+flasks)
    check('unassigned product and old leaf URL survive parent deletion',flask_name in grid(public))
    shot(public,'leaf-still-shoppable-after-unassignment')
    # Rename keeps stable major URL; grouped navigation works on mobile.
    goto(staff,'/admin/major-categories/edit/'+apparel)
    staff.locator('[name="Name"]').fill('Lifestyle '+TAG)
    staff.locator('[name="Description"]').fill('Updated group; same permanent link')
    submit(staff,'form[action*="edit"] button[type="submit"]')
    goto(public,'/products?majorCategoryId='+apparel)
    check('rename keeps stable URL and refreshes group labels','Lifestyle '+TAG in public.inner_text('body') and mug_name in grid(public))
    shot(public,'renamed-major-stable-link')
    public.set_viewport_size({'width':390,'height':844});goto(public,'/products')
    check('mobile category navigation visible',public.locator('#cats').is_visible())
    public.locator('#cats').get_by_text('Lifestyle '+TAG,exact=True).click();public.wait_for_load_state('networkidle')
    check('mobile major navigation works',mug_name in grid(public))
    shot(public,'mobile-major-discovery')
    print(f'ALL {len(checks)} MAJOR-CATEGORY E2E CHECKS PASSED; {seq} screenshots; TAG={TAG}',flush=True)
    browser.close()
