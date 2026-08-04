import{q as Ce,cC as ve,ag as o,x as ue,y as u,ah as k,aw as I,ac as z,d as J,am as U,z as x,ak as pe,A as fe,C as Q,ao as me,E as ke,g as W,r as xe,aF as ye,ae as h,ap as Pe,cD as K,F as Ie,aE as ze,G as Se,c as F,j as L,t as A,m as q,c1 as G,h as N}from"./index-BxhoMKrB.js";import{_ as Be}from"./_plugin-vue_export-helper-DlAUqK2U.js";function $e(l){const{textColor2:c,primaryColorHover:r,primaryColorPressed:p,primaryColor:s,infoColor:d,successColor:n,warningColor:t,errorColor:i,baseColor:f,borderColor:m,opacityDisabled:b,tagColor:S,closeIconColor:y,closeIconColorHover:v,closeIconColorPressed:e,borderRadiusSmall:a,fontSizeMini:C,fontSizeTiny:g,fontSizeSmall:B,fontSizeMedium:$,heightMini:_,heightTiny:H,heightSmall:R,heightMedium:M,closeColorHover:E,closeColorPressed:T,buttonColor2Hover:j,buttonColor2Pressed:w,fontWeightStrong:O}=l;return Object.assign(Object.assign({},ve),{closeBorderRadius:a,heightTiny:_,heightSmall:H,heightMedium:R,heightLarge:M,borderRadius:a,opacityDisabled:b,fontSizeTiny:C,fontSizeSmall:g,fontSizeMedium:B,fontSizeLarge:$,fontWeightStrong:O,textColorCheckable:c,textColorHoverCheckable:c,textColorPressedCheckable:c,textColorChecked:f,colorCheckable:"#0000",colorHoverCheckable:j,colorPressedCheckable:w,colorChecked:s,colorCheckedHover:r,colorCheckedPressed:p,border:`1px solid ${m}`,textColor:c,color:S,colorBordered:"rgb(250, 250, 252)",closeIconColor:y,closeIconColorHover:v,closeIconColorPressed:e,closeColorHover:E,closeColorPressed:T,borderPrimary:`1px solid ${o(s,{alpha:.3})}`,textColorPrimary:s,colorPrimary:o(s,{alpha:.12}),colorBorderedPrimary:o(s,{alpha:.1}),closeIconColorPrimary:s,closeIconColorHoverPrimary:s,closeIconColorPressedPrimary:s,closeColorHoverPrimary:o(s,{alpha:.12}),closeColorPressedPrimary:o(s,{alpha:.18}),borderInfo:`1px solid ${o(d,{alpha:.3})}`,textColorInfo:d,colorInfo:o(d,{alpha:.12}),colorBorderedInfo:o(d,{alpha:.1}),closeIconColorInfo:d,closeIconColorHoverInfo:d,closeIconColorPressedInfo:d,closeColorHoverInfo:o(d,{alpha:.12}),closeColorPressedInfo:o(d,{alpha:.18}),borderSuccess:`1px solid ${o(n,{alpha:.3})}`,textColorSuccess:n,colorSuccess:o(n,{alpha:.12}),colorBorderedSuccess:o(n,{alpha:.1}),closeIconColorSuccess:n,closeIconColorHoverSuccess:n,closeIconColorPressedSuccess:n,closeColorHoverSuccess:o(n,{alpha:.12}),closeColorPressedSuccess:o(n,{alpha:.18}),borderWarning:`1px solid ${o(t,{alpha:.35})}`,textColorWarning:t,colorWarning:o(t,{alpha:.15}),colorBorderedWarning:o(t,{alpha:.12}),closeIconColorWarning:t,closeIconColorHoverWarning:t,closeIconColorPressedWarning:t,closeColorHoverWarning:o(t,{alpha:.12}),closeColorPressedWarning:o(t,{alpha:.18}),borderError:`1px solid ${o(i,{alpha:.23})}`,textColorError:i,colorError:o(i,{alpha:.1}),colorBorderedError:o(i,{alpha:.08}),closeIconColorError:i,closeIconColorHoverError:i,closeIconColorPressedError:i,closeColorHoverError:o(i,{alpha:.12}),closeColorPressedError:o(i,{alpha:.18})})}const _e={common:Ce,self:$e},He={color:Object,type:{type:String,default:"default"},round:Boolean,size:String,closable:Boolean,disabled:{type:Boolean,default:void 0}},Re=ue("tag",`
 --n-close-margin: var(--n-close-margin-top) var(--n-close-margin-right) var(--n-close-margin-bottom) var(--n-close-margin-left);
 white-space: nowrap;
 position: relative;
 box-sizing: border-box;
 cursor: default;
 display: inline-flex;
 align-items: center;
 flex-wrap: nowrap;
 padding: var(--n-padding);
 border-radius: var(--n-border-radius);
 color: var(--n-text-color);
 background-color: var(--n-color);
 transition: 
 border-color .3s var(--n-bezier),
 background-color .3s var(--n-bezier),
 color .3s var(--n-bezier),
 box-shadow .3s var(--n-bezier),
 opacity .3s var(--n-bezier);
 line-height: 1;
 height: var(--n-height);
 font-size: var(--n-font-size);
`,[u("strong",`
 font-weight: var(--n-font-weight-strong);
 `),k("border",`
 pointer-events: none;
 position: absolute;
 left: 0;
 right: 0;
 top: 0;
 bottom: 0;
 border-radius: inherit;
 border: var(--n-border);
 transition: border-color .3s var(--n-bezier);
 `),k("icon",`
 display: flex;
 margin: 0 4px 0 0;
 color: var(--n-text-color);
 transition: color .3s var(--n-bezier);
 font-size: var(--n-avatar-size-override);
 `),k("avatar",`
 display: flex;
 margin: 0 6px 0 0;
 `),k("close",`
 margin: var(--n-close-margin);
 transition:
 background-color .3s var(--n-bezier),
 color .3s var(--n-bezier);
 `),u("round",`
 padding: 0 calc(var(--n-height) / 3);
 border-radius: calc(var(--n-height) / 2);
 `,[k("icon",`
 margin: 0 4px 0 calc((var(--n-height) - 8px) / -2);
 `),k("avatar",`
 margin: 0 6px 0 calc((var(--n-height) - 8px) / -2);
 `),u("closable",`
 padding: 0 calc(var(--n-height) / 4) 0 calc(var(--n-height) / 3);
 `)]),u("icon, avatar",[u("round",`
 padding: 0 calc(var(--n-height) / 3) 0 calc(var(--n-height) / 2);
 `)]),u("disabled",`
 cursor: not-allowed !important;
 opacity: var(--n-opacity-disabled);
 `),u("checkable",`
 cursor: pointer;
 box-shadow: none;
 color: var(--n-text-color-checkable);
 background-color: var(--n-color-checkable);
 `,[I("disabled",[z("&:hover","background-color: var(--n-color-hover-checkable);",[I("checked","color: var(--n-text-color-hover-checkable);")]),z("&:active","background-color: var(--n-color-pressed-checkable);",[I("checked","color: var(--n-text-color-pressed-checkable);")])]),u("checked",`
 color: var(--n-text-color-checked);
 background-color: var(--n-color-checked);
 `,[I("disabled",[z("&:hover","background-color: var(--n-color-checked-hover);"),z("&:active","background-color: var(--n-color-checked-pressed);")])])])]),Me=Object.assign(Object.assign(Object.assign({},Q.props),He),{bordered:{type:Boolean,default:void 0},checked:Boolean,checkable:Boolean,strong:Boolean,triggerClickOnClose:Boolean,onClose:[Array,Function],onMouseenter:Function,onMouseleave:Function,"onUpdate:checked":Function,onUpdateChecked:Function,internalCloseFocusable:{type:Boolean,default:!0},internalCloseIsButtonTag:{type:Boolean,default:!0},onCheckedChange:Function}),Ee=Se("n-tag"),Ve=J({name:"Tag",props:Me,slots:Object,setup(l){const c=xe(null),{mergedBorderedRef:r,mergedClsPrefixRef:p,inlineThemeDisabled:s,mergedRtlRef:d,mergedComponentPropsRef:n}=fe(l),t=W(()=>{var e,a;return l.size||((a=(e=n==null?void 0:n.value)===null||e===void 0?void 0:e.Tag)===null||a===void 0?void 0:a.size)||"medium"}),i=Q("Tag","-tag",Re,_e,l,p);Ie(Ee,{roundRef:ze(l,"round")});function f(){if(!l.disabled&&l.checkable){const{checked:e,onCheckedChange:a,onUpdateChecked:C,"onUpdate:checked":g}=l;C&&C(!e),g&&g(!e),a&&a(!e)}}function m(e){if(l.triggerClickOnClose||e.stopPropagation(),!l.disabled){const{onClose:a}=l;a&&ye(a,e)}}const b={setTextContent(e){const{value:a}=c;a&&(a.textContent=e)}},S=me("Tag",d,p),y=W(()=>{const{type:e,color:{color:a,textColor:C}={}}=l,g=t.value,{common:{cubicBezierEaseInOut:B},self:{padding:$,closeMargin:_,borderRadius:H,opacityDisabled:R,textColorCheckable:M,textColorHoverCheckable:E,textColorPressedCheckable:T,textColorChecked:j,colorCheckable:w,colorHoverCheckable:O,colorPressedCheckable:X,colorChecked:Y,colorCheckedHover:Z,colorCheckedPressed:ee,closeBorderRadius:oe,fontWeightStrong:re,[h("colorBordered",e)]:le,[h("closeSize",g)]:ae,[h("closeIconSize",g)]:ce,[h("fontSize",g)]:se,[h("height",g)]:D,[h("color",e)]:ne,[h("textColor",e)]:te,[h("border",e)]:ie,[h("closeIconColor",e)]:V,[h("closeIconColorHover",e)]:de,[h("closeIconColorPressed",e)]:he,[h("closeColorHover",e)]:ge,[h("closeColorPressed",e)]:be}}=i.value,P=Pe(_);return{"--n-font-weight-strong":re,"--n-avatar-size-override":`calc(${D} - 8px)`,"--n-bezier":B,"--n-border-radius":H,"--n-border":ie,"--n-close-icon-size":ce,"--n-close-color-pressed":be,"--n-close-color-hover":ge,"--n-close-border-radius":oe,"--n-close-icon-color":V,"--n-close-icon-color-hover":de,"--n-close-icon-color-pressed":he,"--n-close-icon-color-disabled":V,"--n-close-margin-top":P.top,"--n-close-margin-right":P.right,"--n-close-margin-bottom":P.bottom,"--n-close-margin-left":P.left,"--n-close-size":ae,"--n-color":a||(r.value?le:ne),"--n-color-checkable":w,"--n-color-checked":Y,"--n-color-checked-hover":Z,"--n-color-checked-pressed":ee,"--n-color-hover-checkable":O,"--n-color-pressed-checkable":X,"--n-font-size":se,"--n-height":D,"--n-opacity-disabled":R,"--n-padding":$,"--n-text-color":C||te,"--n-text-color-checkable":M,"--n-text-color-checked":j,"--n-text-color-hover-checkable":E,"--n-text-color-pressed-checkable":T}}),v=s?ke("tag",W(()=>{let e="";const{type:a,color:{color:C,textColor:g}={}}=l;return e+=a[0],e+=t.value[0],C&&(e+=`a${K(C)}`),g&&(e+=`b${K(g)}`),r.value&&(e+="c"),e}),y,l):void 0;return Object.assign(Object.assign({},b),{rtlEnabled:S,mergedClsPrefix:p,contentRef:c,mergedBordered:r,handleClick:f,handleCloseClick:m,cssVars:s?void 0:y,themeClass:v==null?void 0:v.themeClass,onRender:v==null?void 0:v.onRender})},render(){var l,c;const{mergedClsPrefix:r,rtlEnabled:p,closable:s,color:{borderColor:d}={},round:n,onRender:t,$slots:i}=this;t==null||t();const f=U(i.avatar,b=>b&&x("div",{class:`${r}-tag__avatar`},b)),m=U(i.icon,b=>b&&x("div",{class:`${r}-tag__icon`},b));return x("div",{class:[`${r}-tag`,this.themeClass,{[`${r}-tag--rtl`]:p,[`${r}-tag--strong`]:this.strong,[`${r}-tag--disabled`]:this.disabled,[`${r}-tag--checkable`]:this.checkable,[`${r}-tag--checked`]:this.checkable&&this.checked,[`${r}-tag--round`]:n,[`${r}-tag--avatar`]:f,[`${r}-tag--icon`]:m,[`${r}-tag--closable`]:s}],style:this.cssVars,onClick:this.handleClick,onMouseenter:this.onMouseenter,onMouseleave:this.onMouseleave},m||f,x("span",{class:`${r}-tag__content`,ref:"contentRef"},(c=(l=this.$slots).default)===null||c===void 0?void 0:c.call(l)),!this.checkable&&s?x(pe,{clsPrefix:r,class:`${r}-tag__close`,disabled:this.disabled,onClick:this.handleCloseClick,focusable:this.internalCloseFocusable,round:n,isButtonTag:this.internalCloseIsButtonTag,absolute:!0}):null,!this.checkable&&this.mergedBordered?x("div",{class:`${r}-tag__border`,style:{borderColor:d}}):null)}}),Te={class:"page-header"},je={class:"page-header-main"},we={class:"page-title"},Oe={key:0,class:"page-subtitle"},We={key:0,class:"page-header-actions"},Fe=J({__name:"PageHeader",props:{title:{},subtitle:{}},setup(l){return(c,r)=>(N(),F("div",Te,[L("div",je,[L("h2",we,A(l.title),1),l.subtitle?(N(),F("p",Oe,A(l.subtitle),1)):q("",!0)]),c.$slots.actions||c.$slots.default?(N(),F("div",We,[G(c.$slots,"actions",{},()=>[G(c.$slots,"default",{},void 0,!0)],!0)])):q("",!0)]))}}),Ue=Be(Fe,[["__scopeId","data-v-46cde0e5"]]);export{Ve as N,Ue as P};
