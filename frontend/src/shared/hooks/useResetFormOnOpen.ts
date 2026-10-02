import type { FormInstance } from 'antd';
import { useEffect } from 'react';

/**
 * Makes a dialog's form show its current `initialValues` every time the dialog opens. The form instance outlives the
 * dialog's content (`destroyOnHidden`), so two cases must be covered:
 *
 * - The dialog is reopened before its close animation has finished (a quick second click, a slow machine): the form is
 *   still mounted and holds the previous values - for an edit dialog the values of another record under the new
 *   record's id. This hook resets it.
 * - The dialog was closed completely: its form was destroyed and is mounted again one render after this effect. The
 *   new form takes its `initialValues` only for what the instance does not hold already, so the instance must be empty
 *   by then: **the dialog's `Form` needs `clearOnDestroy`**. Resetting here would do the opposite - an unmounted form
 *   still knows the previous `initialValues` and would be filled with them - so nothing is done in this case.
 *
 * @param opening What the dialog is open for (the edited record, a state object, or `true`); falsy while closed. The
 *                form is reset whenever this changes to a truthy value.
 */
export function useResetFormOnOpen<Values>(form: FormInstance<Values>, opening: unknown): void {
  useEffect(() => {
    // A mounted form has registered fields; a destroyed one has none.
    if (opening && form.getFieldsError().length > 0) {
      form.resetFields();
    }
  }, [form, opening]);
}
